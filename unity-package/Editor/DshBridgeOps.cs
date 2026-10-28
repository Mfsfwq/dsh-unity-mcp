// DSH Unity Bridge —— 操作实现。
// 每个 op 都是「JSON 参数进 → 可序列化对象出」，全部在主线程执行。
// 新增操作：在 Dispatch 的 switch 里加分支，然后同步更新 tools/unity-bridge/README.md。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace DshBridge
{
    public static class DshBridgeOps
    {
        public static object Dispatch(string op, Dictionary<string, object> p)
        {
            DshBridgeCore.RecordOp(op, null);
            switch (op)
            {
                case "ping": return Ping();
                case "scene": return SceneInfo();
                case "open_scene": return OpenScene(p);
                case "assets": return FindAssets(p);
                case "quit": return Quit(p);
                case "hierarchy": return Hierarchy(p);
                case "find": return Find(p);
                case "create": return Create(p);
                case "delete": return Delete(p);
                case "rename": return Rename(p);
                case "set_active": return SetActive(p);
                case "transform": return SetTransform(p);
                case "add_component": return AddComponent(p);
                case "remove_component": return RemoveComponent(p);
                case "set": return SetProperty(p);
                case "save": return Save(p);
                case "screenshot": return Screenshot(p);
                case "play": return PlayMode(p);
                case "menu": return Menu(p);
                case "select": return Select(p);
                case "invoke": return Invoke(p);
                case "log": return Log(p);
                default: throw new Exception("未知 op: " + op + "（可用：ping/scene/open_scene/assets/hierarchy/find/create/delete/rename/set_active/transform/add_component/remove_component/set/save/screenshot/play/menu/select/invoke/log/quit）");
            }
        }

        // ---------------- 基础信息 ----------------
        static object Ping()
        {
            return DshBridgeCore.Describe();
        }

        static object SceneInfo()
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            int objects = 0;
            foreach (var go in roots) objects += go.GetComponentsInChildren<Transform>(true).Length;
            return new Dictionary<string, object>
            {
                { "scenePath", scene.path },
                { "sceneName", scene.name },
                { "isDirty", scene.isDirty },
                { "rootCount", roots.Length },
                { "objectCount", objects }
            };
        }

        static object OpenScene(Dictionary<string, object> p)
        {
            string path = Json.Str(p, "path", null);
            if (string.IsNullOrEmpty(path)) throw new Exception("缺少 path，例如 Assets/Scenes/TestRoom.unity");
            bool additivie = Json.Bool(p, "additive", false);
            var scene = EditorSceneManager.OpenScene(path, additivie ? OpenSceneMode.Additive : OpenSceneMode.Single);
            return new Dictionary<string, object>
            {
                { "scenePath", scene.path },
                { "rootCount", scene.GetRootGameObjects().Length },
                { "isDirty", scene.isDirty }
            };
        }

        static object FindAssets(Dictionary<string, object> p)
        {
            string filter = Json.Str(p, "filter", "");
            string folder = Json.Str(p, "folder", "Assets");
            string typeName = Json.Str(p, "type", null);
            int limit = (int)Json.Num(p, "limit", 100);
            var guids = AssetDatabase.FindAssets(filter, new[] { folder });
            var items = new List<object>();
            foreach (var guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
                string type = obj == null ? "(null)" : obj.GetType().Name;
                if (!string.IsNullOrEmpty(typeName) && type != typeName) continue;
                items.Add(new Dictionary<string, object>
                {
                    { "path", assetPath },
                    { "type", type },
                    { "guid", guid }
                });
                if (items.Count >= limit) break;
            }
            return new Dictionary<string, object> { { "count", items.Count }, { "items", items } };
        }

        static float _quitAt = -1f;
        static int _quitCode;

        static object Quit(Dictionary<string, object> p)
        {
            _quitCode = (int)Json.Num(p, "code", 0);
            _quitAt = Time.realtimeSinceStartup + 0.5f;
            EditorApplication.update += QuitTick;
            return new Dictionary<string, object> { { "quitting", true }, { "code", _quitCode } };
        }

        static void QuitTick()
        {
            if (_quitAt < 0f) return;
            if (Time.realtimeSinceStartup < _quitAt) return;
            _quitAt = -1f;
            EditorApplication.update -= QuitTick;
            EditorApplication.Exit(_quitCode);
        }

        static object Hierarchy(Dictionary<string, object> p)
        {
            int maxDepth = (int)Json.Num(p, "depth", 4);
            int budget = (int)Json.Num(p, "budget", 600);
            bool includeComponents = Json.Bool(p, "components", true);
            var scene = SceneManager.GetActiveScene();
            var roots = new List<object>();
            int startBudget = budget;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (budget <= 0) break;
                roots.Add(Node(go, 0, maxDepth, ref budget, includeComponents));
            }
            return new Dictionary<string, object>
            {
                { "scenePath", scene.path },
                { "nodes", roots },
                { "truncated", budget <= 0 },
                { "budgetUsed", startBudget - budget }
            };
        }

        static Dictionary<string, object> Node(GameObject go, int depth, int maxDepth, ref int budget, bool includeComponents)
        {
            budget--;
            var d = new Dictionary<string, object>
            {
                { "name", go.name },
                { "path", BuildPath(go) },
                { "active", go.activeSelf },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "position", Vec(go.transform.position) }
            };
            if (includeComponents)
            {
                var comps = new List<object>();
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    comps.Add(DescribeComponent(c));
                }
                d["components"] = comps;
            }
            if (depth < maxDepth && go.transform.childCount > 0 && budget > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    if (budget <= 0) break;
                    children.Add(Node(go.transform.GetChild(i).gameObject, depth + 1, maxDepth, ref budget, includeComponents));
                }
                d["children"] = children;
            }
            return d;
        }

        static object DescribeComponent(Component c)
        {
            var d = new Dictionary<string, object> { { "type", c.GetType().Name } };
            var go = c.gameObject;

            var sr = c as SpriteRenderer;
            if (sr != null)
            {
                d["sprite"] = sr.sprite == null ? null : AssetDatabase.GetAssetPath(sr.sprite);
                d["sortingLayer"] = sr.sortingLayerName;
                d["order"] = sr.sortingOrder;
                d["enabled"] = sr.enabled;
                d["flipX"] = sr.flipX;
                return d;
            }

            var animator = c as Animator;
            if (animator != null)
            {
                d["controller"] = animator.runtimeAnimatorController == null ? null : animator.runtimeAnimatorController.name;
                d["enabled"] = animator.enabled;
                return d;
            }

            var cam = c as Camera;
            if (cam != null)
            {
                d["orthographic"] = cam.orthographic;
                d["size"] = cam.orthographicSize;
                d["depth"] = cam.depth;
                d["clearFlags"] = cam.clearFlags.ToString();
                return d;
            }

            var rb = c as Rigidbody2D;
            if (rb != null)
            {
                d["bodyType"] = rb.bodyType.ToString();
                d["gravityScale"] = rb.gravityScale;
                d["simulated"] = rb.simulated;
                return d;
            }

            var bc = c as BoxCollider2D;
            if (bc != null)
            {
                d["size"] = Vec(bc.size);
                d["offset"] = Vec(bc.offset);
                d["isTrigger"] = bc.isTrigger;
                return d;
            }

            var pc = c as PolygonCollider2D;
            if (pc != null)
            {
                d["points"] = pc.points.Length;
                d["isTrigger"] = pc.isTrigger;
                return d;
            }

            var mono = c as MonoBehaviour;
            if (mono != null && mono.GetType() != typeof(MonoBehaviour))
            {
                d["script"] = mono.GetType().Name;
                return d;
            }

            var tr = c as Transform;
            if (tr != null)
            {
                d["position"] = Vec(tr.position);
                d["localPosition"] = Vec(tr.localPosition);
                d["scale"] = Vec(tr.localScale);
                return d;
            }
            return d;
        }

        static object Find(Dictionary<string, object> p)
        {
            string query = Json.Str(p, "query", "");
            string type = Json.Str(p, "type", null);
            var results = new List<object>();
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                {
                    var go = tr.gameObject;
                    bool match = false;
                    if (!string.IsNullOrEmpty(type))
                    {
                        match = go.GetComponents<Component>().Any(c => c != null && c.GetType().Name == type);
                    }
                    else if (!string.IsNullOrEmpty(query))
                    {
                        match = go.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || BuildPath(go).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    if (!match) continue;
                    results.Add(new Dictionary<string, object>
                    {
                        { "name", go.name },
                        { "path", BuildPath(go) },
                        { "active", go.activeSelf },
                        { "instanceId", go.GetInstanceID() },
                        { "components", go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToList() }
                    });
                    if (results.Count >= 200) break;
                }
            }
            return new Dictionary<string, object> { { "count", results.Count }, { "items", results } };
        }

        // ---------------- 写操作 ----------------
        static object Create(Dictionary<string, object> p)
        {
            string name = Json.Str(p, "name", "GameObject");
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "DSH create " + name);

            string parentPath = Json.Str(p, "parent", null);
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = FindGo(parentPath);
                if (parent == null) throw new Exception("父对象不存在: " + parentPath);
                go.transform.SetParent(parent.transform, false);
            }

            var pos = ParseVec(p, "position");
            if (pos.HasValue) go.transform.position = pos.Value;
            var localPos = ParseVec(p, "localPosition");
            if (localPos.HasValue) go.transform.localPosition = localPos.Value;
            var scale = ParseVec(p, "scale");
            if (scale.HasValue) go.transform.localScale = scale.Value;

            string layer = Json.Str(p, "layer", null);
            if (!string.IsNullOrEmpty(layer))
            {
                int index = LayerMask.NameToLayer(layer);
                if (index < 0) throw new Exception("层不存在: " + layer);
                go.layer = index;
            }

            string tag = Json.Str(p, "tag", null);
            if (!string.IsNullOrEmpty(tag))
            {
                if (Array.IndexOf(UnityEditorInternal.InternalEditorUtility.tags, tag) < 0) throw new Exception("Tag 不存在: " + tag);
                go.tag = tag;
            }

            string spritePath = Json.Str(p, "sprite", null);
            if (!string.IsNullOrEmpty(spritePath))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite == null) throw new Exception("Sprite 未找到: " + spritePath);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = sprite;
                sr.sortingOrder = (int)Json.Num(p, "order", 0);
                string sortingLayer = Json.Str(p, "sortingLayer", null);
                if (!string.IsNullOrEmpty(sortingLayer)) sr.sortingLayerName = sortingLayer;
            }

            MarkSceneDirty();
            DshBridgeCore.RecordOp("create", name);
            return new Dictionary<string, object>
            {
                { "name", go.name },
                { "path", BuildPath(go) },
                { "instanceId", go.GetInstanceID() }
            };
        }

        static object Delete(Dictionary<string, object> p)
        {
            var go = Require(Json.Str(p, "path", Json.Str(p, "name", null)));
            // 名字必须在销毁前取出：DestroyObjectImmediate 之后访问 go.name 会抛 MissingReferenceException
            string name = go.name;
            Undo.DestroyObjectImmediate(go);
            MarkSceneDirty();
            return new Dictionary<string, object> { { "deleted", name } };
        }

        static object Rename(Dictionary<string, object> p)
        {
            var go = Require(Json.Str(p, "path", null));
            string to = Json.Str(p, "to", null);
            if (string.IsNullOrEmpty(to)) throw new Exception("缺少 to");
            Undo.RecordObject(go, "DSH rename");
            go.name = to;
            MarkSceneDirty();
            return new Dictionary<string, object> { { "path", BuildPath(go) } };
        }

        static object SetActive(Dictionary<string, object> p)
        {
            var go = Require(Json.Str(p, "path", null));
            bool active = Json.Bool(p, "active", true);
            Undo.RecordObject(go, "DSH set_active");
            go.SetActive(active);
            MarkSceneDirty();
            return new Dictionary<string, object> { { "path", BuildPath(go) }, { "active", go.activeSelf } };
        }

        static object SetTransform(Dictionary<string, object> p)
        {
            var go = Require(Json.Str(p, "path", null));
            var tr = go.transform;
            Undo.RecordObject(tr, "DSH transform");
            var pos = ParseVec(p, "position");
            if (pos.HasValue) tr.position = pos.Value;
            var localPos = ParseVec(p, "localPosition");
            if (localPos.HasValue) tr.localPosition = localPos.Value;
            var scale = ParseVec(p, "scale");
            if (scale.HasValue) tr.localScale = scale.Value;
            var euler = ParseVec(p, "eulerAngles");
            if (euler.HasValue) tr.eulerAngles = euler.Value;
            MarkSceneDirty();
            return new Dictionary<string, object>
            {
                { "path", BuildPath(go) },
                { "position", Vec(tr.position) },
                { "localPosition", Vec(tr.localPosition) },
                { "scale", Vec(tr.localScale) }
            };
        }

        static object AddComponent(Dictionary<string, object> p)
        {
            var go = Require(Json.Str(p, "path", null));
            string typeName = Json.Str(p, "type", null);
            if (string.IsNullOrEmpty(typeName)) throw new Exception("缺少 type");

            Type type = ResolveType(typeName);
            if (type == null) throw new Exception("找不到类型: " + typeName);
            if (!typeof(Component).IsAssignableFrom(type)) throw new Exception(typeName + " 不是 Component");

            var comp = Undo.AddComponent(go, type);
            MarkSceneDirty();
            return new Dictionary<string, object>
            {
                { "path", BuildPath(go) },
                { "type", comp.GetType().Name }
            };
        }

        static object RemoveComponent(Dictionary<string, object> p)
        {
            var go = Require(Json.Str(p, "path", null));
            string typeName = Json.Str(p, "type", null);
            Type type = ResolveType(typeName);
            if (type == null) throw new Exception("找不到类型: " + typeName);
            var comp = go.GetComponent(type);
            if (comp == null) throw new Exception("对象上没有 " + typeName);
            Undo.DestroyObjectImmediate(comp);
            MarkSceneDirty();
            return new Dictionary<string, object> { { "removed", typeName } };
        }

        static object SetProperty(Dictionary<string, object> p)
        {
            var go = Require(Json.Str(p, "path", null));
            string componentName = Json.Str(p, "component", null);
            string field = Json.Str(p, "field", null);
            if (string.IsNullOrEmpty(field)) throw new Exception("缺少 field");

            Component target = null;
            if (string.IsNullOrEmpty(componentName))
            {
                target = go.transform;
            }
            else
            {
                Type type = ResolveType(componentName);
                if (type == null) throw new Exception("找不到类型: " + componentName);
                target = go.GetComponent(type);
                if (target == null) throw new Exception("对象上没有 " + componentName);
            }

            object raw = p.ContainsKey("value") ? p["value"] : null;
            Undo.RecordObject(target, "DSH set " + field);

            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop != null)
            {
                ApplySerialized(prop, raw);
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(target);
                MarkSceneDirty();
                return new Dictionary<string, object> { { "applied", "serialized" }, { "field", field } };
            }

            // 回退到反射
            var member = FindMember(target.GetType(), field);
            if (member == null) throw new Exception("字段不存在: " + target.GetType().Name + "." + field);
            member.SetValue(target, ConvertTo(raw, member.MemberType));
            EditorUtility.SetDirty(target);
            MarkSceneDirty();
            return new Dictionary<string, object> { { "applied", "reflection" }, { "field", field } };
        }

        static object Save(Dictionary<string, object> p)
        {
            bool all = Json.Bool(p, "all", false);
            if (all) EditorSceneManager.SaveOpenScenes();
            else EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            return new Dictionary<string, object>
            {
                { "saved", true },
                { "scenePath", SceneManager.GetActiveScene().path },
                { "isDirty", SceneManager.GetActiveScene().isDirty }
            };
        }

        static object PlayMode(Dictionary<string, object> p)
        {
            string action = Json.Str(p, "action", "start");
            if (action == "start") EditorApplication.isPlaying = true;
            else if (action == "stop") EditorApplication.isPlaying = false;
            else if (action == "pause") EditorApplication.isPaused = true;
            else if (action == "resume") EditorApplication.isPaused = false;
            else throw new Exception("action 只能是 start/stop/pause/resume");
            return new Dictionary<string, object> { { "action", action }, { "isPlaying", EditorApplication.isPlaying } };
        }

        static object Menu(Dictionary<string, object> p)
        {
            string path = Json.Str(p, "path", null);
            if (string.IsNullOrEmpty(path)) throw new Exception("缺少 path，例如 Tools/MCP Unity/Server Window");
            bool ok = EditorApplication.ExecuteMenuItem(path);
            if (!ok) throw new Exception("菜单项未找到或执行失败: " + path);
            return new Dictionary<string, object> { { "executed", path } };
        }

        static object Select(Dictionary<string, object> p)
        {
            var go = Require(Json.Str(p, "path", null));
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            return new Dictionary<string, object> { { "selected", BuildPath(go) } };
        }

        static object Invoke(Dictionary<string, object> p)
        {
            string typeName = Json.Str(p, "type", null);
            string method = Json.Str(p, "method", null);
            Type type = ResolveType(typeName);
            if (type == null) throw new Exception("找不到类型: " + typeName);
            var m = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (m == null) throw new Exception("找不到方法: " + typeName + "." + method);
            object targetInstance = null;
            var argsList = Json.Arr(p, "args");
            object[] args = argsList == null ? new object[0] : argsList.ToArray();
            var result = m.Invoke(m.IsStatic ? null : targetInstance, args);
            return new Dictionary<string, object> { { "result", result == null ? null : result.ToString() } };
        }

        static object Log(Dictionary<string, object> p)
        {
            int lines = (int)Json.Num(p, "lines", 40);
            string path = Application.consoleLogPath;
            if (!File.Exists(path)) return new Dictionary<string, object> { { "path", path }, { "lines", new List<object>() } };
            var all = File.ReadAllLines(path);
            var tail = all.Skip(Math.Max(0, all.Length - lines)).ToList();
            return new Dictionary<string, object> { { "path", path }, { "total", all.Length }, { "lines", tail.ToList<object>() } };
        }

        static object Screenshot(Dictionary<string, object> p)
        {
            string view = Json.Str(p, "view", "scene");
            int width = (int)Json.Num(p, "width", 1280);
            int height = (int)Json.Num(p, "height", 720);

            Camera cam = null;
            if (view == "game")
            {
                cam = Camera.main;
            }
            else
            {
                if (SceneView.lastActiveSceneView != null && SceneView.lastActiveSceneView.camera != null)
                    cam = SceneView.lastActiveSceneView.camera;
                else
                    cam = Camera.main;
            }
            if (cam == null) throw new Exception("找不到可用相机（场景里没有 MainCamera，且没有激活的 Scene 视图）");

            string outPath = Json.Str(p, "path", null);
            if (string.IsNullOrEmpty(outPath))
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                outPath = Path.Combine(root, "..", "screenshots", "scene_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            }
            outPath = Path.GetFullPath(outPath);
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(outPath, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                if (tex != null) Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
            return new Dictionary<string, object> { { "path", outPath }, { "width", width }, { "height", height }, { "view", view } };
        }

        // ---------------- 工具 ----------------
        static void MarkSceneDirty()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }

        static GameObject Require(string path)
        {
            var go = FindGo(path);
            if (go == null) throw new Exception("对象不存在: " + path);
            return go;
        }

        public static GameObject FindGo(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith("#"))
            {
                int id;
                if (int.TryParse(path.Substring(1), out id)) return EditorUtility.InstanceIDToObject(id) as GameObject;
            }
            var direct = GameObject.Find(path);
            if (direct != null) return direct;

            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == path) return root;
                foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                {
                    if (tr.name == path || BuildPath(tr.gameObject) == path) return tr.gameObject;
                }
            }
            return null;
        }

        public static string BuildPath(GameObject go)
        {
            var parts = new List<string>();
            var t = go.transform;
            while (t != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts.ToArray());
        }

        static object Vec(Vector3 v)
        {
            return new List<object> { v.x, v.y, v.z };
        }

        static Vector3? ParseVec(Dictionary<string, object> p, string key)
        {
            if (p == null || !p.ContainsKey(key)) return null;
            var raw = p[key];
            if (raw == null) return null;

            var arr = raw as List<object>;
            if (arr != null && arr.Count >= 2)
            {
                float x = ToFloat(arr[0]);
                float y = ToFloat(arr[1]);
                float z = arr.Count > 2 ? ToFloat(arr[2]) : 0f;
                return new Vector3(x, y, z);
            }

            var s = raw as string;
            if (s != null)
            {
                var pieces = s.Split(',');
                if (pieces.Length >= 2)
                {
                    float x, y, z = 0f;
                    if (float.TryParse(pieces[0].Trim(), out x) && float.TryParse(pieces[1].Trim(), out y))
                    {
                        if (pieces.Length > 2) float.TryParse(pieces[2].Trim(), out z);
                        return new Vector3(x, y, z);
                    }
                }
                return null;
            }

            var map = raw as Dictionary<string, object>;
            if (map != null)
            {
                return new Vector3(ToFloat(map.ContainsKey("x") ? map["x"] : null), ToFloat(map.ContainsKey("y") ? map["y"] : null), ToFloat(map.ContainsKey("z") ? map["z"] : null));
            }
            return null;
        }

        static float ToFloat(object v)
        {
            if (v == null) return 0f;
            if (v is double) return (float)(double)v;
            if (v is long) return (long)v;
            float f;
            if (float.TryParse(Convert.ToString(v), out f)) return f;
            return 0f;
        }

        static Type ResolveType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.Name == name || t.FullName == name) return t;
                }
            }
            return null;
        }

        static void ApplySerialized(SerializedProperty prop, object raw)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: prop.intValue = (int)ToFloat(raw); break;
                case SerializedPropertyType.Float: prop.floatValue = ToFloat(raw); break;
                case SerializedPropertyType.Boolean: prop.boolValue = raw is bool ? (bool)raw : ToFloat(raw) != 0; break;
                case SerializedPropertyType.String: prop.stringValue = Convert.ToString(raw); break;
                case SerializedPropertyType.Enum: prop.enumValueIndex = (int)ToFloat(raw); break;
                case SerializedPropertyType.Vector2:
                    {
                        var arr = raw as List<object>;
                        if (arr != null) prop.vector2Value = new Vector2(ToFloat(arr[0]), ToFloat(arr[1]));
                        break;
                    }
                case SerializedPropertyType.Vector3:
                    {
                        var arr = raw as List<object>;
                        if (arr != null) prop.vector3Value = new Vector3(ToFloat(arr[0]), ToFloat(arr[1]), arr.Count > 2 ? ToFloat(arr[2]) : 0f);
                        break;
                    }
                case SerializedPropertyType.ObjectReference:
                    {
                        var path = raw as string;
                        if (!string.IsNullOrEmpty(path)) prop.objectReferenceValue = AssetDatabase.LoadAssetAtPath<Object>(path);
                        break;
                    }
                default:
                    throw new Exception("不支持的字段类型: " + prop.propertyType + "（请改用 transform / create 等专用 op）");
            }
        }

        class MemberAccess
        {
            public MemberInfo Info;
            public Type MemberType;
            public void SetValue(object target, object value)
            {
                var f = Info as FieldInfo;
                if (f != null) { f.SetValue(target, value); return; }
                var pr = Info as PropertyInfo;
                if (pr != null) { pr.SetValue(target, value, null); return; }
            }
        }

        static MemberAccess FindMember(Type type, string name)
        {
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return new MemberAccess { Info = f, MemberType = f.FieldType };
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return new MemberAccess { Info = p, MemberType = p.PropertyType };
            return null;
        }

        static object ConvertTo(object raw, Type target)
        {
            if (raw == null) return null;
            if (target.IsInstanceOfType(raw)) return raw;
            if (target == typeof(float)) return ToFloat(raw);
            if (target == typeof(int)) return (int)ToFloat(raw);
            if (target == typeof(bool)) return raw is bool ? (bool)raw : ToFloat(raw) != 0;
            if (target == typeof(string)) return Convert.ToString(raw);
            return Convert.ChangeType(raw, target);
        }
    }
}
