#if MONO
using Newtonsoft.Json;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine.SceneManagement;
using UniverseLib.Runtime;
using UnityExplorer.Config;
using UnityExplorer.CSConsole;
using UnityExplorer.ObjectExplorer;
using UnityExplorer.UI.Panels;

namespace UnityExplorer.AIBridge
{
    // Localhost HTTP JSON API so an external AI agent can inspect and script the game.
    // Endpoints: GET /scene, GET /inspect, POST /execute, GET /logs
    public static class AIBridgeServer
    {
        internal const int DISPATCH_TIMEOUT_MS = 15000;
        const int MAX_VALUE_STRING_LENGTH = 500;

        const int PORT_SCAN_RANGE = 16; // supports many concurrent game instances (networked testing)

        static HttpListener listener;
        internal static int Port { get; private set; }

        public static void Init()
        {
            int basePort = ConfigManager.AI_Bridge_Port.Value;
            if (basePort <= 0)
                return;

            // Multiple game instances: take the first free port in [base, base+16).
            for (int port = basePort; port < basePort + PORT_SCAN_RANGE; port++)
            {
                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Start();
                    Port = port;
                    break;
                }
                catch
                {
                    listener = null;
                }
            }

            if (listener == null)
            {
                ExplorerCore.LogWarning($"AI Bridge failed to start: no free port in [{basePort}, {basePort + PORT_SCAN_RANGE}).");
                return;
            }

            CacheInstanceIdentity();

            Thread thread = new(ListenLoop) { IsBackground = true, Name = "UE-AIBridge" };
            thread.Start();

            ExplorerCore.Log($"AI Bridge listening on http://127.0.0.1:{Port}/");
        }

        static void ListenLoop()
        {
            while (listener != null && listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch
                {
                    return; // listener stopped / game quitting
                }

                try
                {
                    Handle(ctx);
                }
                catch (Exception ex)
                {
                    TryRespond(ctx, 500, new Dictionary<string, object> { { "ok", false }, { "error", ex.ToString() } });
                }
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');

            switch (path)
            {
                case "":
                    TryRespond(ctx, 200, new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "name", $"{ExplorerCore.NAME} AI Bridge" },
                        { "instance", GetInstanceIdentity() },
                        { "endpoints", new List<object>
                            {
                                "GET /docs - full API documentation (markdown)",
                                "POST /mcp - MCP server (JSON-RPC 2.0, Streamable HTTP transport)",
                                "GET /scene?depth=N&max=N - scene hierarchy",
                                "GET /inspect?path=<GameObject/path> - component fields/properties of a GameObject",
                                "GET /inspect?type=<TypeName> - member signatures of a type",
                                "GET /inspect?id=<instanceID> - inspect any UnityEngine.Object by instance id",
                                "GET /search?mode=object|singleton|class&name=&type=&limit=N - find objects, singletons or classes",
                                "GET /hooks | POST /hooks/create | GET /hooks/toggle?sig= | GET /hooks/delete?sig= - Harmony method hooks",
                                "POST /watch?frames=N - body is a C# expression, sampled once per frame",
                                "GET /observers | POST /observers/create?mode= | GET /observers/delete?id= - per-frame observers recording into the event buffer",
                                "GET /events?since=N - drain observer events (cursor)",
                                "POST /wait_for?mode=&timeout=ms - block until an expression becomes true / changes",
                                "GET /inspect_at?x=&y= - world raycast at normalized screen coords (top-left origin)",
                                "GET /freecam?enabled=&x=&y=&z= - toggle/position the free camera",
                                "POST /execute - body is raw C#, evaluated in the REPL",
                                "GET /logs?since=N - log entries from index N",
                                "GET /screenshot?max=N - PNG screenshot of the game (max = optional max dimension, 0 = full)",
                            }
                        },
                    });
                    return;

                case "/docs":
                    TryRespondRaw(ctx, 200, "text/markdown", GetEmbeddedDocs());
                    return;

                case "/scene":
                    {
                        int depth = ParseIntParam(ctx, "depth", 3);
                        int max = ParseIntParam(ctx, "max", 2000);
                        object result = MainThreadDispatcher.Run(() => BuildSceneTree(depth, max), DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/inspect":
                    {
                        string goPath = ctx.Request.QueryString["path"];
                        string typeName = ctx.Request.QueryString["type"];
                        string idRaw = ctx.Request.QueryString["id"];
                        if (string.IsNullOrEmpty(goPath) && string.IsNullOrEmpty(typeName) && string.IsNullOrEmpty(idRaw))
                        {
                            TryRespond(ctx, 400, Error("Provide ?path=<GameObject/path>, ?type=<TypeName> or ?id=<instanceID>."));
                            return;
                        }
                        object result = MainThreadDispatcher.Run(() =>
                        {
                            if (!string.IsNullOrEmpty(idRaw) && int.TryParse(idRaw, out int id))
                                return InspectById(id);
                            if (!string.IsNullOrEmpty(goPath))
                                return InspectGameObject(goPath);
                            return InspectType(typeName);
                        }, DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/search":
                    {
                        string mode = ctx.Request.QueryString["mode"] ?? "object";
                        string name = ctx.Request.QueryString["name"];
                        string type = ctx.Request.QueryString["type"];
                        int limit = ParseIntParam(ctx, "limit", 50);
                        object result = MainThreadDispatcher.Run(() => BridgeTools.SearchObjects(mode, name, type, limit), DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/hooks":
                    {
                        object result = MainThreadDispatcher.Run(BridgeTools.ListHooks, DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/hooks/create":
                    {
                        // POST JSON: { "type": "...", "method": "...", "param_types": [...]?, "patch_code": "..."? }
                        if (ctx.Request.HttpMethod != "POST")
                        {
                            TryRespond(ctx, 405, Error("Use POST with a JSON body: { type, method, param_types?, patch_code? }"));
                            return;
                        }
                        string body;
                        using (StreamReader reader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                            body = reader.ReadToEnd();
                        Newtonsoft.Json.Linq.JObject json = Newtonsoft.Json.Linq.JObject.Parse(body);
                        string hookType = json.Value<string>("type");
                        string hookMethod = json.Value<string>("method");
                        string[] paramTypes = (json["param_types"] as Newtonsoft.Json.Linq.JArray)?.Select(t => t.ToString()).ToArray();
                        string patchCode = json.Value<string>("patch_code");
                        if (string.IsNullOrEmpty(hookType) || string.IsNullOrEmpty(hookMethod))
                        {
                            TryRespond(ctx, 400, Error("'type' and 'method' are required."));
                            return;
                        }
                        object result = MainThreadDispatcher.Run(
                            () => BridgeTools.CreateHook(hookType, hookMethod, paramTypes, patchCode), DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/hooks/toggle":
                case "/hooks/delete":
                    {
                        string sig = ctx.Request.QueryString["sig"];
                        if (string.IsNullOrEmpty(sig))
                        {
                            TryRespond(ctx, 400, Error("Provide ?sig=<hook signature> (from GET /hooks)."));
                            return;
                        }
                        bool toggle = path == "/hooks/toggle";
                        object result = MainThreadDispatcher.Run(
                            () => toggle ? BridgeTools.ToggleHook(sig) : BridgeTools.DeleteHook(sig), DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/watch":
                    {
                        // POST body = C# expression; ?frames=N (default 60, max 600)
                        if (ctx.Request.HttpMethod != "POST")
                        {
                            TryRespond(ctx, 405, Error("Use POST with the C# expression as the request body."));
                            return;
                        }
                        string expression;
                        using (StreamReader reader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                            expression = reader.ReadToEnd();
                        int frames = ParseIntParam(ctx, "frames", 60);
                        int watchTimeout = Math.Max(DISPATCH_TIMEOUT_MS, frames * 100 + 5000);
                        object result = BridgeTools.Watch(expression, frames, watchTimeout);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/observers":
                    {
                        object result = MainThreadDispatcher.Run(Observers.List, DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/observers/create":
                    {
                        // POST body = C# expression; ?mode=change|true (default change)
                        if (ctx.Request.HttpMethod != "POST")
                        {
                            TryRespond(ctx, 405, Error("Use POST with the C# expression as the request body. Optional ?mode=change|true"));
                            return;
                        }
                        string expression;
                        using (StreamReader reader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                            expression = reader.ReadToEnd();
                        string mode = ctx.Request.QueryString["mode"];
                        object result = MainThreadDispatcher.Run(() => Observers.Create(expression, mode), DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/observers/delete":
                    {
                        int obsId = ParseIntParam(ctx, "id", -1);
                        object result = MainThreadDispatcher.Run(() => Observers.Delete(obsId), DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/events":
                    {
                        long since = long.TryParse(ctx.Request.QueryString["since"], out long s) ? s : 0;
                        object result = MainThreadDispatcher.Run(() => Observers.GetEvents(since), DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/wait_for":
                    {
                        // POST body = C# expression; ?mode=true|change (default true), ?timeout=ms (default 30000)
                        if (ctx.Request.HttpMethod != "POST")
                        {
                            TryRespond(ctx, 405, Error("Use POST with the C# expression as the request body. Optional ?mode=true|change&timeout=ms"));
                            return;
                        }
                        string expression;
                        using (StreamReader reader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                            expression = reader.ReadToEnd();
                        string mode = ctx.Request.QueryString["mode"];
                        int timeout = ParseIntParam(ctx, "timeout", 30000);
                        object result = Observers.WaitFor(expression, mode, timeout);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/inspect_at":
                    {
                        string xRaw = ctx.Request.QueryString["x"];
                        string yRaw = ctx.Request.QueryString["y"];
                        if (!float.TryParse(xRaw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                            || !float.TryParse(yRaw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
                        {
                            TryRespond(ctx, 400, Error("Provide ?x=&y= as normalized 0..1 coordinates from the top-left (same orientation as /screenshot)."));
                            return;
                        }
                        object result = MainThreadDispatcher.Run(() => BridgeTools.InspectAt(x, y), DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/freecam":
                    {
                        bool enabled = ctx.Request.QueryString["enabled"] != "false" && ctx.Request.QueryString["enabled"] != "0";
                        float? px = null, py = null, pz = null;
                        if (float.TryParse(ctx.Request.QueryString["x"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fx)) px = fx;
                        if (float.TryParse(ctx.Request.QueryString["y"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fy)) py = fy;
                        if (float.TryParse(ctx.Request.QueryString["z"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fz)) pz = fz;
                        object result = MainThreadDispatcher.Run(() => BridgeTools.Freecam(enabled, px, py, pz), DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/execute":
                    {
                        if (ctx.Request.HttpMethod != "POST")
                        {
                            TryRespond(ctx, 405, Error("Use POST with the C# code as the request body."));
                            return;
                        }
                        string code;
                        using (StreamReader reader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                            code = reader.ReadToEnd();
                        if (string.IsNullOrEmpty(code))
                        {
                            TryRespond(ctx, 400, Error("Empty request body."));
                            return;
                        }
                        object result = MainThreadDispatcher.Run(() => ExecuteCode(code), DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/logs":
                    {
                        int since = ParseIntParam(ctx, "since", 0);
                        object result = MainThreadDispatcher.Run(() => GetLogs(since), DISPATCH_TIMEOUT_MS);
                        TryRespond(ctx, 200, result);
                        return;
                    }

                case "/screenshot":
                    {
                        int maxDim = ParseIntParam(ctx, "max", 0); // 0 = full resolution
                        byte[] png = (byte[])MainThreadDispatcher.RunAtEndOfFrame(() => CaptureScreenshotPng(maxDim), DISPATCH_TIMEOUT_MS);
                        TryRespondBytes(ctx, 200, "image/png", png);
                        return;
                    }

                case "/mcp":
                    McpEndpoint.Handle(ctx);
                    return;

                default:
                    TryRespond(ctx, 404, Error($"Unknown endpoint '{path}'. GET / lists available endpoints."));
                    return;
            }
        }


        #region Scene hierarchy

        internal static object BuildSceneTree(int maxDepth, int maxNodes)
        {
            List<object> scenes = new();
            int nodeCount = 0;
            bool truncated = false;

            List<Scene> loaded = new();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene == default || !scene.isLoaded || !scene.IsValid())
                    continue;
                loaded.Add(scene);
            }
            if (SceneHandler.DontDestroyExists)
                loaded.Add(new Scene { m_Handle = -12 });

            foreach (Scene scene in loaded)
            {
                List<object> roots = new();
                foreach (GameObject go in RuntimeHelper.GetRootGameObjects(scene))
                {
                    if (nodeCount >= maxNodes)
                    {
                        truncated = true;
                        break;
                    }
                    roots.Add(BuildNode(go.transform, maxDepth, maxNodes, ref nodeCount, ref truncated));
                }

                scenes.Add(new Dictionary<string, object>
                {
                    { "name", scene.handle == -12 ? "DontDestroyOnLoad" : scene.name },
                    { "rootObjects", roots },
                });
            }

            // HideAndDontSave pseudo-scene: objects with that flag plus loose Assets/Resources.
            // Placed last so the node cap prefers real scenes.
            {
                List<object> roots = new();
                foreach (UnityEngine.Object obj in RuntimeHelper.FindObjectsOfTypeAll(typeof(GameObject)))
                {
                    if (nodeCount >= maxNodes)
                    {
                        truncated = true;
                        break;
                    }
                    GameObject go = obj.TryCast<GameObject>();
                    if (go == null || go.transform.parent != null || go.scene.IsValid())
                        continue;
                    roots.Add(BuildNode(go.transform, maxDepth, maxNodes, ref nodeCount, ref truncated));
                }
                scenes.Add(new Dictionary<string, object>
                {
                    { "name", "HideAndDontSave" },
                    { "rootObjects", roots },
                });
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "truncated", truncated },
                { "nodeCount", nodeCount },
                { "scenes", scenes },
            };
        }

        static object BuildNode(Transform transform, int remainingDepth, int maxNodes, ref int nodeCount, ref bool truncated)
        {
            nodeCount++;
            GameObject go = transform.gameObject;

            List<object> components = new();
            foreach (Component comp in go.GetComponents<Component>())
            {
                if (comp != null)
                    components.Add(comp.GetType().Name);
            }

            Dictionary<string, object> node = new()
            {
                { "name", go.name },
                { "id", go.GetInstanceID() },
                { "active", go.activeSelf },
                { "components", components },
                { "childCount", transform.childCount },
            };

            if (transform.childCount > 0 && remainingDepth > 0)
            {
                List<object> children = new();
                for (int i = 0; i < transform.childCount; i++)
                {
                    if (nodeCount >= maxNodes)
                    {
                        truncated = true;
                        break;
                    }
                    children.Add(BuildNode(transform.GetChild(i), remainingDepth - 1, maxNodes, ref nodeCount, ref truncated));
                }
                node.Add("children", children);
            }

            return node;
        }

        #endregion


        #region Inspect

        internal static object InspectGameObject(string path)
        {
            GameObject go = ResolveGameObject(path);
            if (go == null)
                return Error($"No GameObject found at path '{path}'.");
            return InspectGameObjectCore(go);
        }

        internal static object InspectById(int id)
        {
            UnityEngine.Object obj = BridgeTools.FindObjectById(id);
            if (obj == null)
                return Error($"No UnityEngine.Object found with instance id {id}.");

            if (obj.TryCast<GameObject>() is GameObject go)
                return InspectGameObjectCore(go);

            Dictionary<string, object> result = new()
            {
                { "ok", true },
                { "name", obj.name },
                { "id", obj.GetInstanceID() },
                { "type", obj.GetActualType().FullName },
                { "members", DumpInstanceMembers(obj, obj.GetActualType()) },
            };
            if (obj.TryCast<Component>() is Component comp && comp.gameObject)
                result.Add("gameObjectPath", GetGameObjectPath(comp.transform));
            return result;
        }

        static object InspectGameObjectCore(GameObject go)
        {
            List<object> components = new();
            foreach (Component comp in go.GetComponents<Component>())
            {
                if (comp == null)
                    continue;

                Type type = comp.GetType();
                components.Add(new Dictionary<string, object>
                {
                    { "type", type.FullName },
                    { "id", comp.GetInstanceID() },
                    { "members", DumpInstanceMembers(comp, type) },
                });
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "name", go.name },
                { "id", go.GetInstanceID() },
                { "path", GetGameObjectPath(go.transform) },
                { "active", go.activeSelf },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "tag", go.tag },
                { "components", components },
            };
        }

        static List<object> DumpInstanceMembers(object instance, Type type)
        {
            List<object> members = new();

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                members.Add(DescribeValueMember("field", field.Name, field.FieldType, () => field.GetValue(instance)));

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (prop.GetIndexParameters().Length > 0 || !prop.CanRead)
                    continue;
                members.Add(DescribeValueMember("property", prop.Name, prop.PropertyType, () => prop.GetValue(instance, null)));
            }

            return members;
        }

        static object DescribeValueMember(string kind, string name, Type type, Func<object> getValue)
        {
            string value;
            try
            {
                object val = getValue();
                value = val?.ToString() ?? "null";
                if (value.Length > MAX_VALUE_STRING_LENGTH)
                    value = value.Substring(0, MAX_VALUE_STRING_LENGTH) + "...";
            }
            catch (Exception ex)
            {
                value = $"<exception: {ex.GetType().Name}>";
            }

            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "name", name },
                { "type", type.FullName },
                { "value", value },
            };
        }

        internal static object InspectType(string typeName)
        {
            Type type = ReflectionUtility.GetTypeByName(typeName);
            if (type == null)
                return Error($"Type '{typeName}' not found.");

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            List<object> fields = new();
            foreach (FieldInfo field in type.GetFields(flags))
                fields.Add($"{(field.IsStatic ? "static " : "")}{field.FieldType.Name} {field.Name}");

            List<object> properties = new();
            foreach (PropertyInfo prop in type.GetProperties(flags))
                properties.Add($"{prop.PropertyType.Name} {prop.Name} {{ {(prop.CanRead ? "get; " : "")}{(prop.CanWrite ? "set; " : "")}}}");

            List<object> methods = new();
            foreach (MethodInfo method in type.GetMethods(flags))
            {
                if (method.IsSpecialName) // skip property/event accessors
                    continue;
                string[] args = method.GetParameters()
                    .Select(p => $"{p.ParameterType.Name} {p.Name}")
                    .ToArray();
                methods.Add($"{(method.IsStatic ? "static " : "")}{method.ReturnType.Name} {method.Name}({string.Join(", ", args)})");
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "type", type.FullName },
                { "assembly", type.Assembly.GetName().Name },
                { "baseType", type.BaseType?.FullName },
                { "fields", fields },
                { "properties", properties },
                { "methods", methods },
            };
        }

        // Resolves "Root/Child/GrandChild" across all loaded scenes, including inactive objects.
        static GameObject ResolveGameObject(string path)
        {
            path = path.Trim('/');
            int firstSlash = path.IndexOf('/');
            string rootName = firstSlash < 0 ? path : path.Substring(0, firstSlash);
            string childPath = firstSlash < 0 ? null : path.Substring(firstSlash + 1);

            List<Scene> scenes = new();
            for (int i = 0; i < SceneManager.sceneCount; i++)
                scenes.Add(SceneManager.GetSceneAt(i));
            if (SceneHandler.DontDestroyExists)
                scenes.Add(new Scene { m_Handle = -12 });

            foreach (Scene scene in scenes)
            {
                if (scene.handle != -12 && (!scene.isLoaded || !scene.IsValid()))
                    continue;

                foreach (GameObject root in RuntimeHelper.GetRootGameObjects(scene))
                {
                    if (root.name != rootName)
                        continue;
                    if (childPath == null)
                        return root;
                    Transform child = root.transform.Find(childPath);
                    if (child != null)
                        return child.gameObject;
                }
            }
            return null;
        }

        internal static string GetGameObjectPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }

        #endregion


        #region Shared endpoint handlers (REST + MCP)

        internal static object ExecuteCode(string code)
        {
            ConsoleController.EvalResult eval = ConsoleController.EvaluateCapture(code);
            return new Dictionary<string, object>
            {
                { "ok", eval.Ok },
                { "result", eval.Result },
                { "error", eval.Error },
            };
        }

        internal static object GetLogs(int since)
        {
            List<object> entries = new();
            int total = LogPanel.LogCount;
            for (int i = Math.Max(0, since); i < total; i++)
            {
                LogPanel.LogInfo log = LogPanel.GetLog(i);
                entries.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "type", log.type.ToString() },
                    { "message", log.message },
                    { "utc", log.utc.ToString("o") },
                });
            }
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "total", total },
                { "entries", entries },
            };
        }

        // Must run at end of frame (MainThreadDispatcher.RunAtEndOfFrame).
        internal static byte[] CaptureScreenshotPng(int maxDim)
        {
            Texture2D tex = new(Screen.width, Screen.height, TextureFormat.RGB24, false);
            try
            {
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply(false, false);

                if (maxDim > 0 && (tex.width > maxDim || tex.height > maxDim))
                {
                    Texture2D scaled = Downscale(tex, maxDim);
                    UnityEngine.Object.Destroy(tex);
                    tex = scaled;
                }

                string tmp = Path.Combine(Path.GetTempPath(), $"ue_aibridge_screenshot_{Guid.NewGuid():N}.png");
                try
                {
                    TextureHelper.SaveTextureAsPNG(tex, tmp);
                    return File.ReadAllBytes(tmp);
                }
                finally
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }

        static Texture2D Downscale(Texture2D src, int maxDim)
        {
            float scale = (float)maxDim / Mathf.Max(src.width, src.height);
            int width = Mathf.Max(1, Mathf.RoundToInt(src.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(src.height * scale));

            Texture2D result = new(width, height, TextureFormat.RGB24, false);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    result.SetPixel(x, y, src.GetPixelBilinear((x + 0.5f) / width, (y + 0.5f) / height));
            result.Apply(false, false);
            return result;
        }

        #endregion


        #region Helpers

        static string cachedProductName;
        static string cachedSteamName;

        // Main thread only; called once from Init.
        static void CacheInstanceIdentity()
        {
            try { cachedProductName = Application.productName; } catch { }

            // Steam persona name, if Steamworks is available (helps label host vs client).
            try
            {
                Type steamFriends = ReflectionUtility.GetTypeByName("Steamworks.SteamFriends");
                MethodInfo getName = steamFriends?.GetMethod("GetPersonaName", BindingFlags.Static | BindingFlags.Public);
                if (getName != null)
                    cachedSteamName = getName.Invoke(null, null)?.ToString();
            }
            catch { }
        }

        // Identifies this game instance so agents can tell multiple running copies apart.
        // Thread-safe (uses cached values).
        internal static Dictionary<string, object> GetInstanceIdentity()
        {
            return new Dictionary<string, object>
            {
                { "port", Port },
                { "pid", System.Diagnostics.Process.GetCurrentProcess().Id },
                { "product", cachedProductName },
                { "steamName", cachedSteamName },
                { "utc", DateTime.UtcNow.ToString("o") },
            };
        }

        static Dictionary<string, object> Error(string message)
            => new() { { "ok", false }, { "error", message } };

        static int ParseIntParam(HttpListenerContext ctx, string name, int defaultValue)
        {
            string raw = ctx.Request.QueryString[name];
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out int value))
                return value;
            return defaultValue;
        }

        static string cachedDocs;

        static string GetEmbeddedDocs()
        {
            if (cachedDocs != null)
                return cachedDocs;

            try
            {
                using Stream stream = typeof(AIBridgeServer).Assembly.GetManifestResourceStream("UnityExplorer.AIBridge.BridgeDocs.md");
                using StreamReader reader = new(stream, Encoding.UTF8);
                cachedDocs = reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                cachedDocs = $"Embedded documentation could not be loaded: {ex.Message}\nGET / lists available endpoints.";
            }
            return cachedDocs;
        }

        internal static void TryRespondBytes(HttpListenerContext ctx, int status, string contentType, byte[] bytes)
        {
            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                // client disconnected, nothing to do
            }
        }

        internal static void TryRespondRaw(HttpListenerContext ctx, int status, string contentType, string body)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentEncoding = Encoding.UTF8;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                // client disconnected, nothing to do
            }
        }

        static void TryRespond(HttpListenerContext ctx, int status, object payload)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentEncoding = Encoding.UTF8;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                // client disconnected, nothing to do
            }
        }

        #endregion
    }
}
#endif
