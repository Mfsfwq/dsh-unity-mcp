// DSH Unity Bridge —— 编辑器窗口（可选，命令行/HTTP 也能全用）。
// 菜单：Tools → DSH Unity Bridge
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DshBridge
{
    public class DshBridgeWindow : EditorWindow
    {
        Vector2 _scroll;
        int _portInput;
        bool _autoStart;
        string _token;
        string _lastPing = "";

        [MenuItem("Tools/DSH Unity Bridge")]
        public static void Open()
        {
            var w = GetWindow<DshBridgeWindow>("DSH Bridge");
            w.minSize = new Vector2(420, 320);
            w.Reload();
        }

        void Reload()
        {
            var s = DshBridgeCore.Settings;
            _portInput = s.Port;
            _autoStart = s.AutoStart;
            _token = s.Token;
            Repaint();
        }

        void OnEnable()
        {
            Reload();
            EditorApplication.update += OnTick;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnTick;
        }

        void OnTick()
        {
            // 运行状态可能被自动启动/停止改变，低频刷新即可
            if (Time.realtimeSinceStartup - _lastTick > 1f)
            {
                _lastTick = Time.realtimeSinceStartup;
                Repaint();
            }
        }

        float _lastTick;

        void OnGUI()
        {
            EditorGUILayout.LabelField("DSH Unity Bridge", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "本插件让 DSH 通过本机 HTTP 直接驱动编辑器（读场景 / 建对象 / 改组件 / 截图 / 存场景）。\n" +
                "零第三方依赖，不需要 Unity 6，也不需要 com.unity.pipeline。",
                MessageType.Info);

            EditorGUILayout.Space();
            var s = DshBridgeCore.Settings;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("状态", GUILayout.Width(48));
                if (DshBridgeCore.IsRunning)
                    EditorGUILayout.LabelField("运行中  http://127.0.0.1:" + DshBridgeCore.Port + "/rpc", EditorStyles.boldLabel);
                else
                    EditorGUILayout.LabelField("未启动", EditorStyles.boldLabel);
            }

            if (!string.IsNullOrEmpty(DshBridgeCore.LastError))
                EditorGUILayout.HelpBox(DshBridgeCore.LastError, MessageType.Error);

            EditorGUILayout.Space();
            _portInput = EditorGUILayout.IntField("端口", _portInput);
            _autoStart = EditorGUILayout.Toggle("编辑器加载时自动启动", _autoStart);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(DshBridgeCore.IsRunning ? "重启" : "启动"))
                {
                    var settings = DshBridgeCore.Settings;
                    settings.Port = _portInput;
                    settings.AutoStart = _autoStart;
                    DshBridgeCore.SaveSettings(settings);
                    DshBridgeCore.Restart(_portInput);
                    Reload();
                }
                if (GUILayout.Button("停止"))
                {
                    DshBridgeCore.Stop();
                }
                if (GUILayout.Button("保存设置"))
                {
                    var settings = DshBridgeCore.Settings;
                    settings.Port = _portInput;
                    settings.AutoStart = _autoStart;
                    DshBridgeCore.SaveSettings(settings);
                    Debug.Log("[DshBridge] 设置已保存，重启编辑器后生效（或点重启）");
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Token", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(_token ?? "", EditorStyles.textField, GUILayout.Height(18));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("复制 token")) EditorGUIUtility.systemCopyBuffer = _token ?? "";
                if (GUILayout.Button("重新生成")) DshBridgeCore.RegenerateToken();
                if (GUILayout.Button("打开设置文件")) EditorUtility.RevealInFinder(DshBridgeCore.SettingsPath);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("连接测试", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("本机 /health"))
                {
                    _lastPing = DshBridgeCore.SelfCheck();
                }
                if (GUILayout.Button("打印 DSH 命令"))
                {
                    string cmd = "node tools/unity-bridge/bridge.mjs ping --project \"" +
                                 System.IO.Path.GetDirectoryName(Application.dataPath) + "\"";
                    Debug.Log("[DshBridge] " + cmd);
                    EditorGUIUtility.systemCopyBuffer = cmd;
                    _lastPing = "命令已复制到剪贴板";
                }
            }
            if (!string.IsNullOrEmpty(_lastPing))
                EditorGUILayout.HelpBox(_lastPing, MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("最近操作（最多 50 条）", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(120));
            var ops = DshBridgeCore.GetRecentOps();
            if (ops.Count == 0) EditorGUILayout.LabelField("（尚无）");
            for (int i = ops.Count - 1; i >= 0; i--) EditorGUILayout.LabelField(ops[i], EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
        }
    }
}
