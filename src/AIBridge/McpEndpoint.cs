#if MONO
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;

namespace UnityExplorer.AIBridge
{
    // Minimal MCP server over the Streamable HTTP transport (JSON-RPC 2.0 on POST /mcp).
    // Stateless: no sessions, no SSE streaming, plain JSON responses.
    // Register with e.g.: claude mcp add --transport http uch-game http://127.0.0.1:7311/mcp
    public static class McpEndpoint
    {
        const string PROTOCOL_VERSION = "2025-06-18";

        public static void Handle(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                // No SSE stream to offer; also covers DELETE (no sessions to end).
                ctx.Response.StatusCode = 405;
                ctx.Response.Close();
                return;
            }

            string body;
            using (StreamReader reader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                body = reader.ReadToEnd();

            JObject request;
            try
            {
                request = JObject.Parse(body);
            }
            catch (Exception ex)
            {
                RespondJson(ctx, 400, RpcError(null, -32700, $"Parse error: {ex.Message}"));
                return;
            }

            JToken id = request["id"];
            string method = request.Value<string>("method");
            JObject params_ = request["params"] as JObject;

            if (string.IsNullOrEmpty(method))
            {
                RespondJson(ctx, 400, RpcError(id, -32600, "Invalid request: missing method."));
                return;
            }

            // Notifications get no response body.
            if (method.StartsWith("notifications/"))
            {
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }

            try
            {
                switch (method)
                {
                    case "initialize":
                        {
                            string clientProtocol = params_?.Value<string>("protocolVersion") ?? PROTOCOL_VERSION;
                            RespondJson(ctx, 200, RpcResult(id, new JObject
                            {
                                ["protocolVersion"] = clientProtocol,
                                ["capabilities"] = new JObject { ["tools"] = new JObject() },
                                ["serverInfo"] = new JObject
                                {
                                    ["name"] = "uch-ai-bridge",
                                    ["version"] = ExplorerCore.VERSION,
                                },
                                ["instructions"] = "Tools for inspecting and scripting the live Ultimate Chicken Horse game process. " +
                                    "Typical loop: get_scene (shallow) to orient, inspect_gameobject/inspect_type to narrow down, " +
                                    "execute_csharp to probe and mutate, get_logs to observe effects.",
                            }));
                            return;
                        }

                    case "ping":
                        RespondJson(ctx, 200, RpcResult(id, new JObject()));
                        return;

                    case "tools/list":
                        RespondJson(ctx, 200, RpcResult(id, new JObject { ["tools"] = BuildToolList() }));
                        return;

                    case "tools/call":
                        {
                            string name = params_?.Value<string>("name");
                            JObject args = params_?["arguments"] as JObject ?? new JObject();
                            if (string.IsNullOrEmpty(name))
                            {
                                RespondJson(ctx, 400, RpcError(id, -32602, "Missing tool name."));
                                return;
                            }
                            RespondJson(ctx, 200, CallTool(id, name, args));
                            return;
                        }

                    default:
                        RespondJson(ctx, 200, RpcError(id, -32601, $"Method not found: {method}"));
                        return;
                }
            }
            catch (Exception ex)
            {
                RespondJson(ctx, 200, RpcError(id, -32603, $"Internal error: {ex}"));
            }
        }


        #region Tools

        static JArray BuildToolList()
        {
            return new JArray
            {
                Tool("get_scene",
                    "Get the scene hierarchy of the running game (all loaded scenes incl. DontDestroyOnLoad). " +
                    "Returns a tree of { name, active, components, childCount, children }. " +
                    "Start shallow (depth=1) to orient; childCount > returned children means more below the depth limit.",
                    new JObject
                    {
                        ["depth"] = Prop("integer", "Child recursion depth below root objects (default 2)."),
                        ["max"] = Prop("integer", "Node cap; response sets truncated=true when hit (default 500)."),
                    }),

                Tool("inspect_gameobject",
                    "Inspect a live GameObject by hierarchy path (slash-joined, root object first, e.g. 'MainMenu_Artwork/Horse'). " +
                    "Returns all components with current field/property values. Finds inactive objects too.",
                    new JObject
                    {
                        ["path"] = Prop("string", "Slash-joined hierarchy path of the GameObject."),
                    }, "path"),

                Tool("inspect_type",
                    "Get field/property/method signatures of a type (short or namespace-qualified name; " +
                    "resolved across all loaded assemblies incl. the game's Assembly-CSharp). No values, signatures only.",
                    new JObject
                    {
                        ["type"] = Prop("string", "Type name, e.g. 'PlayerCharacterController' or 'UnityEngine.Camera'."),
                    }, "type"),

                Tool("execute_csharp",
                    "Execute C# code inside the running game (Unity main thread, REPL semantics). " +
                    "A trailing expression without ';' is returned as the result. State persists between calls. " +
                    "Default usings: System, System.Linq, System.Text, System.Collections(.Generic), System.Reflection, UnityEngine, UniverseLib. " +
                    "Game types (Assembly-CSharp) are directly usable. HarmonyLib is available for patching.",
                    new JObject
                    {
                        ["code"] = Prop("string", "Raw C# code to evaluate."),
                    }, "code"),

                Tool("find_objects",
                    "Search the running game for objects. mode='object' (default): live UnityEngine.Objects by name substring and/or type; " +
                    "mode='singleton': classes with a live Instance field (great for finding managers); mode='class': type names. " +
                    "Results include instance ids usable with inspect_id.",
                    new JObject
                    {
                        ["name"] = Prop("string", "Name filter (substring, case-insensitive). Optional."),
                        ["type"] = Prop("string", "UnityEngine.Object-derived type filter for mode=object, e.g. 'PlayerCharacterController'. Optional."),
                        ["mode"] = Prop("string", "'object' (default), 'singleton', or 'class'."),
                        ["limit"] = Prop("integer", "Max results (default 50)."),
                    }),

                Tool("inspect_id",
                    "Inspect any UnityEngine.Object by its instance id (from get_scene, find_objects or inspect_gameobject results). " +
                    "Unambiguous alternative to path addressing when names are duplicated.",
                    new JObject
                    {
                        ["id"] = Prop("integer", "Unity instance id."),
                    }, "id"),

                Tool("create_hook",
                    "Attach a Harmony hook to a method. Without patch_code, a default Postfix is generated that logs every call " +
                    "with instance, arguments and return value to the log (read via get_logs) — instant call tracing. " +
                    "With patch_code, provide C# method(s) named Prefix (bool or void), Postfix, Finalizer and/or Transpiler, " +
                    "e.g. 'static void Prefix(ref float __0) { __0 *= 2f; }'. Hooks persist until deleted.",
                    new JObject
                    {
                        ["type"] = Prop("string", "Declaring type name."),
                        ["method"] = Prop("string", "Method name."),
                        ["param_types"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = new JObject { ["type"] = "string" },
                            ["description"] = "Parameter type names to disambiguate overloads. Optional.",
                        },
                        ["patch_code"] = Prop("string", "Custom patch source. Optional (default = call logger)."),
                    }, "type", "method"),

                Tool("list_hooks",
                    "List active/inactive Harmony hooks with their signatures and patch source.",
                    new JObject()),

                Tool("toggle_hook",
                    "Enable/disable a hook by signature (from list_hooks or create_hook).",
                    new JObject { ["signature"] = Prop("string", "Hook signature.") }, "signature"),

                Tool("delete_hook",
                    "Unpatch and remove a hook by signature.",
                    new JObject { ["signature"] = Prop("string", "Hook signature.") }, "signature"),

                Tool("watch",
                    "Evaluate a C# expression once per frame for N frames and return the sampled values with timestamps. " +
                    "Ideal for physics/movement debugging (e.g. a player's velocity during a jump). " +
                    "Expression must be a single expression without trailing ';'.",
                    new JObject
                    {
                        ["expression"] = Prop("string", "C# expression to sample, e.g. 'UnityEngine.Object.FindObjectOfType<Rigidbody2D>().velocity'."),
                        ["frames"] = Prop("integer", "Number of frames to sample (default 60, max 600)."),
                    }, "expression"),

                Tool("inspect_at",
                    "World-physics raycast at a screen position: 'what object is at this pixel?'. " +
                    "Coordinates are normalized 0..1 from the TOP-LEFT, matching screenshot orientation " +
                    "(so a point at the center of a screenshot is x=0.5, y=0.5 regardless of resolution). " +
                    "Only hits colliders; UI elements are not found.",
                    new JObject
                    {
                        ["x"] = Prop("number", "0..1 from left."),
                        ["y"] = Prop("number", "0..1 from top."),
                    }, "x", "y"),

                Tool("freecam",
                    "Enable/disable UnityExplorer's free camera, optionally setting its world position. " +
                    "Useful to frame screenshots of specific areas. Disable when done to restore the game camera.",
                    new JObject
                    {
                        ["enabled"] = Prop("boolean", "true to enable, false to restore the game camera."),
                        ["x"] = Prop("number", "World position X. Optional."),
                        ["y"] = Prop("number", "World position Y. Optional."),
                        ["z"] = Prop("number", "World position Z. Optional."),
                    }, "enabled"),

                Tool("screenshot",
                    "Take a PNG screenshot of the game window (captured after rendering). " +
                    "Use this to see the current visual state of the game. " +
                    "Defaults to a downscaled image (max dimension 1280) to save tokens; pass max=0 for full resolution.",
                    new JObject
                    {
                        ["max"] = Prop("integer", "Max width/height in pixels; image is scaled down to fit. 0 = full resolution (default 1280)."),
                    }),

                Tool("get_logs",
                    "Read the UnityExplorer log (incl. Unity Debug.Log if the 'Log Unity Debug' config is enabled, " +
                    "and output from your own execute_csharp Debug.Log calls). " +
                    "Returns { total, entries }. Poll incrementally by passing the previous 'total' as 'since'.",
                    new JObject
                    {
                        ["since"] = Prop("integer", "Return entries from this index onward (default 0)."),
                    }),
            };
        }

        static JObject CallTool(JToken id, string name, JObject args)
        {
            object result;
            try
            {
                switch (name)
                {
                    case "get_scene":
                        {
                            int depth = args.Value<int?>("depth") ?? 2;
                            int max = args.Value<int?>("max") ?? 500;
                            result = MainThreadDispatcher.Run(() => AIBridgeServer.BuildSceneTree(depth, max), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            break;
                        }
                    case "inspect_gameobject":
                        {
                            string path = args.Value<string>("path");
                            if (string.IsNullOrEmpty(path))
                                return RpcError(id, -32602, "Missing required argument 'path'.");
                            result = MainThreadDispatcher.Run(() => AIBridgeServer.InspectGameObject(path), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            break;
                        }
                    case "inspect_type":
                        {
                            string type = args.Value<string>("type");
                            if (string.IsNullOrEmpty(type))
                                return RpcError(id, -32602, "Missing required argument 'type'.");
                            result = MainThreadDispatcher.Run(() => AIBridgeServer.InspectType(type), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            break;
                        }
                    case "execute_csharp":
                        {
                            string code = args.Value<string>("code");
                            if (string.IsNullOrEmpty(code))
                                return RpcError(id, -32602, "Missing required argument 'code'.");
                            result = MainThreadDispatcher.Run(() => AIBridgeServer.ExecuteCode(code), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            break;
                        }
                    case "find_objects":
                        {
                            string mode = args.Value<string>("mode") ?? "object";
                            string nameFilter = args.Value<string>("name");
                            string typeFilter = args.Value<string>("type");
                            int limit = args.Value<int?>("limit") ?? 50;
                            result = MainThreadDispatcher.Run(() => BridgeTools.SearchObjects(mode, nameFilter, typeFilter, limit), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            break;
                        }
                    case "inspect_id":
                        {
                            int? objId = args.Value<int?>("id");
                            if (objId == null)
                                return RpcError(id, -32602, "Missing required argument 'id'.");
                            result = MainThreadDispatcher.Run(() => AIBridgeServer.InspectById(objId.Value), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            break;
                        }
                    case "create_hook":
                        {
                            string hookType = args.Value<string>("type");
                            string hookMethod = args.Value<string>("method");
                            if (string.IsNullOrEmpty(hookType) || string.IsNullOrEmpty(hookMethod))
                                return RpcError(id, -32602, "Missing required arguments 'type' and/or 'method'.");
                            string[] paramTypes = (args["param_types"] as JArray)?.Select(t => t.ToString()).ToArray();
                            string patchCode = args.Value<string>("patch_code");
                            result = MainThreadDispatcher.Run(() => BridgeTools.CreateHook(hookType, hookMethod, paramTypes, patchCode), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            break;
                        }
                    case "list_hooks":
                        result = MainThreadDispatcher.Run(BridgeTools.ListHooks, AIBridgeServer.DISPATCH_TIMEOUT_MS);
                        break;
                    case "toggle_hook":
                    case "delete_hook":
                        {
                            string sig = args.Value<string>("signature");
                            if (string.IsNullOrEmpty(sig))
                                return RpcError(id, -32602, "Missing required argument 'signature'.");
                            bool toggle = name == "toggle_hook";
                            result = MainThreadDispatcher.Run(() => toggle ? BridgeTools.ToggleHook(sig) : BridgeTools.DeleteHook(sig), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            break;
                        }
                    case "watch":
                        {
                            string expression = args.Value<string>("expression");
                            if (string.IsNullOrEmpty(expression))
                                return RpcError(id, -32602, "Missing required argument 'expression'.");
                            int frames = args.Value<int?>("frames") ?? 60;
                            int watchTimeout = Math.Max(AIBridgeServer.DISPATCH_TIMEOUT_MS, frames * 100 + 5000);
                            result = BridgeTools.Watch(expression, frames, watchTimeout);
                            break;
                        }
                    case "inspect_at":
                        {
                            float? x = args.Value<float?>("x");
                            float? y = args.Value<float?>("y");
                            if (x == null || y == null)
                                return RpcError(id, -32602, "Missing required arguments 'x' and/or 'y'.");
                            result = MainThreadDispatcher.Run(() => BridgeTools.InspectAt(x.Value, y.Value), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            break;
                        }
                    case "freecam":
                        {
                            bool? enabled = args.Value<bool?>("enabled");
                            if (enabled == null)
                                return RpcError(id, -32602, "Missing required argument 'enabled'.");
                            float? fx = args.Value<float?>("x");
                            float? fy = args.Value<float?>("y");
                            float? fz = args.Value<float?>("z");
                            result = MainThreadDispatcher.Run(() => BridgeTools.Freecam(enabled.Value, fx, fy, fz), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            break;
                        }
                    case "screenshot":
                        {
                            int maxDim = args.Value<int?>("max") ?? 1280;
                            byte[] png = (byte[])MainThreadDispatcher.RunAtEndOfFrame(
                                () => AIBridgeServer.CaptureScreenshotPng(maxDim), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            return RpcResult(id, new JObject
                            {
                                ["content"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["type"] = "image",
                                        ["data"] = Convert.ToBase64String(png),
                                        ["mimeType"] = "image/png",
                                    },
                                },
                                ["isError"] = false,
                            });
                        }
                    case "get_logs":
                        {
                            int since = args.Value<int?>("since") ?? 0;
                            result = MainThreadDispatcher.Run(() => AIBridgeServer.GetLogs(since), AIBridgeServer.DISPATCH_TIMEOUT_MS);
                            break;
                        }
                    default:
                        return RpcError(id, -32602, $"Unknown tool: {name}");
                }
            }
            catch (Exception ex)
            {
                // Tool execution failure -> tool-level error, not a protocol error.
                return RpcResult(id, ToolResult($"Tool execution failed: {ex.Message}", true));
            }

            string json = JsonConvert.SerializeObject(result);
            bool isError = result is Dictionary<string, object> dict
                && dict.TryGetValue("ok", out object ok)
                && ok is bool okBool && !okBool;

            return RpcResult(id, ToolResult(json, isError));
        }

        static JObject ToolResult(string text, bool isError)
        {
            return new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
                ["isError"] = isError,
            };
        }

        static JObject Tool(string name, string description, JObject properties, params string[] required)
        {
            JObject schema = new()
            {
                ["type"] = "object",
                ["properties"] = properties,
            };
            if (required.Length > 0)
                schema["required"] = new JArray(required);

            return new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = schema,
            };
        }

        static JObject Prop(string type, string description)
            => new() { ["type"] = type, ["description"] = description };

        #endregion


        #region JSON-RPC plumbing

        static JObject RpcResult(JToken id, JObject result)
            => new() { ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull(), ["result"] = result };

        static JObject RpcError(JToken id, int code, string message)
            => new()
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            };

        static void RespondJson(HttpListenerContext ctx, int status, JObject payload)
            => AIBridgeServer.TryRespondRaw(ctx, status, "application/json", payload.ToString(Formatting.None));

        #endregion
    }
}
#endif
