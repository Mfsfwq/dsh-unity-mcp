// DSH Unity Bridge —— 编辑器侧 HTTP 服务。
// 设计要点：
//  · 只用 TcpListener + 手写最小 HTTP/1.1，不碰 HttpListener（避免 URL ACL 需要管理员）；
//  · Unity API 只能在主线程调，所以请求统一入队、由 EditorApplication.update 泵到主线程执行；
//  · 所有请求要 Bearer token（token 落在 UserSettings/，不进版本库）；
//  · 零第三方依赖：JSON 用自带的 DshBridgeJson。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace DshBridge
{
    [Serializable]
    public class BridgeSettings
    {
        public int Port = 8090;
        public string Token = "";
        public bool AutoStart = true;
    }

    [InitializeOnLoad]
    public static class DshBridgeCore
    {
        public const string PackageId = "com.dsh.unity-bridge";
        public const string Version = "0.1.0";

        static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();
        static readonly List<string> RecentOps = new List<string>();
        static readonly object RecentLock = new object();

        static TcpListener _listener;
        static Thread _acceptThread;
        static volatile bool _running;
        static BridgeSettings _settings;

        public static string LastError = "";
        public static int Port { get; private set; }

        public static bool IsRunning { get { return _running; } }

        public static BridgeSettings Settings
        {
            get
            {
                if (_settings == null) _settings = LoadSettings();
                return _settings;
            }
        }

        public static string SettingsPath
        {
            get
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                string dir = Path.Combine(root, "UserSettings");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "UnityBridge.json");
            }
        }

        static DshBridgeCore()
        {
            EditorApplication.update += Pump;
            EditorApplication.delayCall += OnEditorReady;
        }

        static bool _readyRan;

        static void OnEditorReady()
        {
            // delayCall 与 Pump 兜底都会调这里，只允许自启一次，否则会 Start→Stop→Start 重复一遍
            if (_readyRan) return;
            _readyRan = true;
            var s = Settings;
            if (s.AutoStart) Start(s.Port);
        }

        // ---------------- 设置持久化 ----------------
        public static BridgeSettings LoadSettings()
        {
            var s = new BridgeSettings();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var parsed = Json.AsObject(Json.Parse(File.ReadAllText(SettingsPath)));
                    if (parsed != null)
                    {
                        s.Port = (int)Json.Num(parsed, "Port", 8090);
                        s.Token = Json.Str(parsed, "Token", "");
                        s.AutoStart = Json.Bool(parsed, "AutoStart", true);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DshBridge] 读取设置失败，使用默认值: " + e.Message);
            }

            if (string.IsNullOrEmpty(s.Token))
            {
                s.Token = Guid.NewGuid().ToString("N");
                SaveSettings(s);
            }
            if (s.Port <= 0 || s.Port > 65535) s.Port = 8090;
            return s;
        }

        public static void SaveSettings(BridgeSettings s)
        {
            var map = new Dictionary<string, object>
            {
                { "Port", s.Port },
                { "Token", s.Token },
                { "AutoStart", s.AutoStart }
            };
            File.WriteAllText(SettingsPath, Json.Stringify(map), new UTF8Encoding(false));
            _settings = s;
        }

        // ---------------- 服务生命周期 ----------------
        public static void Start(int port)
        {
            if (_running) Stop();
            _settings = LoadSettings();
            _settings.Port = port;
            SaveSettings(_settings);

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
            }
            catch (SocketException e)
            {
                // 端口占用时自动 +1 重试一次，方便多工程并存
                try
                {
                    port = port + 1;
                    _listener = new TcpListener(IPAddress.Loopback, port);
                    _listener.Start();
                    _settings.Port = port;
                    SaveSettings(_settings);
                }
                catch (Exception inner)
                {
                    LastError = "监听 " + port + " 失败: " + e.Message + " / " + inner.Message;
                    Debug.LogError("[DshBridge] " + LastError);
                    return;
                }
            }

            Port = port;
            _running = true;
            LastError = "";
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "DshBridge.Accept";
            _acceptThread.Start();
            Debug.Log("[DshBridge] 已启动 http://127.0.0.1:" + port + "/rpc （token 见 " + SettingsPath + "）");
        }

        public static void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            Debug.Log("[DshBridge] 已停止");
        }

        public static void Restart(int port) { Stop(); Start(port); }

        static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { if (!_running) return; Thread.Sleep(50); continue; }

                ThreadPool.QueueUserWorkItem(delegate (object state) { HandleClient((TcpClient)state); }, client);
            }
        }

        // ---------------- HTTP 处理 ----------------
        static void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;
                    var stream = client.GetStream();

                    byte[] raw = ReadRequest(stream, out int headerEnd);
                    if (raw == null) return;

                    string headerText = Encoding.UTF8.GetString(raw, 0, headerEnd);
                    string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] requestLine = lines[0].Split(' ');
                    if (requestLine.Length < 2) { WriteResponse(stream, 400, ErrorJson("请求行非法")); return; }

                    string method = requestLine[0];
                    string path = requestLine[1];
                    string target = path.Split('?')[0];

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                    }

                    int contentLength = 0;
                    string cl;
                    if (headers.TryGetValue("Content-Length", out cl)) int.TryParse(cl, out contentLength);

                    byte[] bodyBytes = new byte[contentLength];
                    int offset = headerEnd + 4;
                    int alreadyHave = raw.Length - offset;
                    if (alreadyHave > 0)
                    {
                        int copy = Math.Min(alreadyHave, contentLength);
                        Array.Copy(raw, offset, bodyBytes, 0, copy);
                        int remaining = contentLength - copy;
                        if (remaining > 0) ReadExactly(stream, bodyBytes, copy, remaining);
                    }
                    else if (contentLength > 0)
                    {
                        ReadExactly(stream, bodyBytes, 0, contentLength);
                    }

                    // 无鉴权的健康检查，方便 curl 探活
                    if (target == "/health")
                    {
                        WriteResponse(stream, 200, Json.Stringify(new Dictionary<string, object>
                        {
                            { "ok", true },
                            { "data", Describe() }
                        }));
                        return;
                    }

                    if (target != "/rpc") { WriteResponse(stream, 404, ErrorJson("未知路径，请用 POST /rpc")); return; }

                    string auth = null;
                    headers.TryGetValue("Authorization", out auth);
                    if (!IsAuthorized(auth, headers, path))
                    {
                        WriteResponse(stream, 401, ErrorJson("token 无效"));
                        return;
                    }

                    string body = bodyBytes.Length > 0 ? Encoding.UTF8.GetString(bodyBytes) : "";
                    var payload = Json.AsObject(Json.Parse(body));
                    if (payload == null) { WriteResponse(stream, 400, ErrorJson("请求体必须是 JSON 对象")); return; }

                    string op = Json.Str(payload, "op", "");
                    var parameters = Json.AsObject(payload.ContainsKey("params") ? payload["params"] : null);

                    object result = null;
                    string error = null;
                    bool completed = RunOnMainThread(delegate
                    {
                        try { result = DshBridgeOps.Dispatch(op, parameters); }
                        catch (Exception e) { error = e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace; }
                    });

                    if (!completed) { WriteResponse(stream, 504, ErrorJson("主线程 30s 内没有执行完（编辑器可能卡住或正在编译）")); return; }
                    if (error != null) { WriteResponse(stream, 500, ErrorJson(error)); return; }

                    WriteResponse(stream, 200, Json.Stringify(new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "op", op },
                        { "data", result }
                    }));
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                    Debug.LogWarning("[DshBridge] 处理请求失败: " + e);
                }
            }
        }

        static bool IsAuthorized(string authHeader, Dictionary<string, string> headers, string fullPath)
        {
            string expected = Settings.Token;
            if (string.IsNullOrEmpty(expected)) return false;
            if (!string.IsNullOrEmpty(authHeader))
            {
                string v = authHeader.Trim();
                if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) v = v.Substring(7).Trim();
                if (v == expected) return true;
            }
            int q = fullPath.IndexOf("token=", StringComparison.Ordinal);
            if (q >= 0)
            {
                string v = fullPath.Substring(q + 6).Split('&')[0];
                if (v == expected) return true;
            }
            return false;
        }

        static byte[] ReadRequest(Stream stream, out int headerEnd)
        {
            headerEnd = -1;
            var ms = new MemoryStream();
            var buffer = new byte[4096];
            while (headerEnd < 0)
            {
                int n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) return null;
                ms.Write(buffer, 0, n);
                byte[] data = ms.ToArray();
                headerEnd = IndexOf(data, new byte[] { 13, 10, 13, 10 });
                if (ms.Length > 1024 * 1024) return null;
            }
            return ms.ToArray();
        }

        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = stream.Read(buffer, offset, count);
                if (n <= 0) throw new IOException("请求体不完整");
                offset += n;
                count -= n;
            }
        }

        static void WriteResponse(Stream stream, int status, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json ?? "{}");
            string reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 401 ? "Unauthorized" : status == 404 ? "Not Found" : status == 504 ? "Gateway Timeout" : "Error";
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        static string ErrorJson(string message)
        {
            return Json.Stringify(new Dictionary<string, object> { { "ok", false }, { "error", message } });
        }

        // ---------------- 主线程泵 ----------------
        static bool _bootstrapped;

        static void Pump()
        {
            // 兜底自启：批处理模式（-batchmode）下 delayCall 可能在 -quit 前来不及跑，
            // 所以第一次 update 时补一次初始化。
            if (!_bootstrapped)
            {
                _bootstrapped = true;
                OnEditorReady();
            }

            Action action;
            while (MainThreadQueue.TryDequeue(out action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogError("[DshBridge] 主线程执行异常: " + e); }
            }
        }

        static bool RunOnMainThread(Action action)
        {
            using (var done = new ManualResetEventSlim(false))
            {
                MainThreadQueue.Enqueue(delegate
                {
                    try { action(); }
                    finally { done.Set(); }
                });
                return done.Wait(TimeSpan.FromSeconds(30));
            }
        }

        public static void RegenerateToken()
        {
            var s = Settings;
            s.Token = Guid.NewGuid().ToString("N");
            SaveSettings(s);
            Debug.Log("[DshBridge] 已重新生成 token（需要重启服务才生效）");
        }

        // 窗口里的「本机 /health」按钮：走回环 TCP 自检一次
        public static string SelfCheck()
        {
            if (!_running) return "服务未启动，先点「启动」";
            try
            {
                using (var c = new TcpClient())
                {
                    c.Connect(IPAddress.Loopback, Port);
                    var stream = c.GetStream();
                    byte[] send = Encoding.ASCII.GetBytes("GET /health HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                    stream.Write(send, 0, send.Length);
                    stream.Flush();
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string text = reader.ReadToEnd();
                        int idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        string body = idx >= 0 ? text.Substring(idx + 4) : text;
                        return "OK: " + (body.Length > 400 ? body.Substring(0, 400) : body);
                    }
                }
            }
            catch (Exception e)
            {
                return "自检失败: " + e.Message;
            }
        }

        public static void RecordOp(string op, string detail)
        {
            lock (RecentLock)
            {
                RecentOps.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + op + "  " + (detail ?? ""));
                if (RecentOps.Count > 50) RecentOps.RemoveAt(0);
            }
        }

        public static List<string> GetRecentOps()
        {
            lock (RecentLock) return new List<string>(RecentOps);
        }

        public static object Describe()
        {
            return new Dictionary<string, object>
            {
                { "package", PackageId },
                { "version", Version },
                { "port", Port },
                { "running", _running },
                { "unity", Application.unityVersion },
                { "project", Path.GetDirectoryName(Application.dataPath) },
                { "isPlaying", EditorApplication.isPlaying },
                { "lastError", LastError }
            };
        }
    }
}
