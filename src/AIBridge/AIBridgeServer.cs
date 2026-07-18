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

        static HttpListener listener;

        public static void Init()
        {
            int port = ConfigManager.AI_Bridge_Port.Value;
            if (port <= 0)
                return;

            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"AI Bridge failed to start on port {port}: {ex}");
                listener = null;
                return;
            }

            Thread thread = new(ListenLoop) { IsBackground = true, Name = "UE-AIBridge" };
            thread.Start();

            ExplorerCore.Log($"AI Bridge listening on http://127.0.0.1:{port}/");
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
                        { "endpoints", new List<object>
                            {
                                "GET /docs - full API documentation (markdown)",
                                "POST /mcp - MCP server (JSON-RPC 2.0, Streamable HTTP transport)",
                                "GET /scene?depth=N&max=N - scene hierarchy",
                                "GET /inspect?path=<GameObject/path> - component fields/properties of a GameObject",
                                "GET /inspect?type=<TypeName> - member signatures of a type",
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
                        if (string.IsNullOrEmpty(goPath) && string.IsNullOrEmpty(typeName))
                        {
                            TryRespond(ctx, 400, Error("Provide either ?path=<GameObject/path> or ?type=<TypeName>."));
                            return;
                        }
                        object result = MainThreadDispatcher.Run(
                            () => !string.IsNullOrEmpty(goPath) ? InspectGameObject(goPath) : InspectType(typeName),
                            DISPATCH_TIMEOUT_MS);
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

            List<object> components = new();
            foreach (Component comp in go.GetComponents<Component>())
            {
                if (comp == null)
                    continue;

                Type type = comp.GetType();
                List<object> members = new();

                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    members.Add(DescribeValueMember("field", field.Name, field.FieldType, () => field.GetValue(comp)));

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (prop.GetIndexParameters().Length > 0 || !prop.CanRead)
                        continue;
                    members.Add(DescribeValueMember("property", prop.Name, prop.PropertyType, () => prop.GetValue(comp, null)));
                }

                components.Add(new Dictionary<string, object>
                {
                    { "type", type.FullName },
                    { "members", members },
                });
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "name", go.name },
                { "path", GetGameObjectPath(go.transform) },
                { "active", go.activeSelf },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "tag", go.tag },
                { "components", components },
            };
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

        static string GetGameObjectPath(Transform transform)
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
