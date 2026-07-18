#if MONO
using HarmonyLib;
using System.Collections;
using System.Threading;
using UnityExplorer.CSConsole;
using UnityExplorer.Hooks;
using UnityExplorer.ObjectExplorer;

namespace UnityExplorer.AIBridge
{
    // Second wave of bridge tools: object search, instance-id addressing,
    // method hooks (via UnityExplorer's HookInstance), per-frame watch,
    // screen raycast and freecam. All methods must run on the main thread.
    public static class BridgeTools
    {
        #region Search

        internal static object SearchObjects(string mode, string name, string typeName, int limit)
        {
            List<object> raw;
            switch (mode)
            {
                case "singleton":
                    raw = SearchProvider.InstanceSearch(name);
                    break;
                case "class":
                    raw = SearchProvider.ClassSearch(name);
                    break;
                default:
                    raw = SearchProvider.UnityObjectSearch(name, typeName, ChildFilter.Any, SceneFilter.Any);
                    break;
            }

            List<object> results = new();
            foreach (object obj in raw)
            {
                if (results.Count >= limit)
                    break;

                switch (obj)
                {
                    case Type type:
                        results.Add(new Dictionary<string, object>
                        {
                            { "type", type.FullName },
                            { "assembly", type.Assembly.GetName().Name },
                        });
                        break;

                    case UnityEngine.Object uObj:
                        {
                            GameObject go = uObj as GameObject ?? (uObj as Component)?.gameObject;
                            Dictionary<string, object> entry = new()
                            {
                                { "name", uObj.name },
                                { "type", uObj.GetActualType().FullName },
                                { "id", uObj.GetInstanceID() },
                            };
                            if (go != null)
                                entry.Add("path", AIBridgeServer.GetGameObjectPath(go.transform));
                            results.Add(entry);
                            break;
                        }

                    default: // singleton instances are plain objects
                        results.Add(new Dictionary<string, object>
                        {
                            { "type", obj.GetActualType().FullName },
                            { "value", obj.ToString() },
                        });
                        break;
                }
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "total", raw.Count },
                { "returned", results.Count },
                { "results", results },
            };
        }

        internal static UnityEngine.Object FindObjectById(int id)
        {
            foreach (UnityEngine.Object obj in RuntimeHelper.FindObjectsOfTypeAll(typeof(UnityEngine.Object)))
            {
                if (obj.GetInstanceID() == id)
                    return obj;
            }
            return null;
        }

        #endregion


        #region Hooks

        internal static object CreateHook(string typeName, string methodName, string[] paramTypes, string patchCode)
        {
            Type type = ReflectionUtility.GetTypeByName(typeName);
            if (type == null)
                return Error($"Type '{typeName}' not found.");

            List<MethodInfo> candidates = type.GetMethods(ReflectionUtility.FLAGS)
                .Where(it => it.Name == methodName && !it.IsGenericMethod)
                .ToList();

            if (paramTypes != null)
            {
                candidates = candidates.Where(it =>
                {
                    ParameterInfo[] parameters = it.GetParameters();
                    if (parameters.Length != paramTypes.Length)
                        return false;
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (!string.Equals(parameters[i].ParameterType.Name, paramTypes[i], StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(parameters[i].ParameterType.FullName, paramTypes[i], StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    return true;
                }).ToList();
            }

            if (candidates.Count == 0)
                return Error($"No non-generic method '{methodName}' found on '{type.FullName}'" +
                    (paramTypes != null ? " matching the given parameter types." : "."));

            if (candidates.Count > 1)
            {
                return new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", $"Ambiguous: {candidates.Count} overloads. Disambiguate with param_types (list of parameter type names)." },
                    { "overloads", candidates.Select(it => (object)it.FullDescription()).ToList() },
                };
            }

            MethodInfo method = candidates[0];
            string signature = method.FullDescription();

            if (HookList.hookedSignatures.Contains(signature))
                return Error($"Method is already hooked: {signature}. Delete the hook first, or use toggle.");

            HookInstance hook = new(method);
            if (!hook.Enabled)
                return Error("Failed to compile/apply the default hook (see game log for compiler output).");

            if (!string.IsNullOrEmpty(patchCode))
            {
                if (!hook.CompileAndGenerateProcessor(patchCode))
                {
                    // default hook was unpatched by the failed recompile; don't keep a broken hook
                    return Error("Custom patch code failed to compile (see game log for compiler output). Hook not created.");
                }
                hook.PatchSourceCode = patchCode;
                hook.Patch();
            }

            HookList.hookedSignatures.Add(signature);
            HookList.currentHooks.Add(signature, hook);
            RefreshHookPanel();

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "signature", signature },
                { "enabled", hook.Enabled },
                { "patchSource", hook.PatchSourceCode },
            };
        }

        internal static object ListHooks()
        {
            List<object> hooks = new();
            foreach (string sig in HookList.currentHooks.Keys.Cast<string>())
            {
                HookInstance hook = (HookInstance)HookList.currentHooks[sig];
                hooks.Add(new Dictionary<string, object>
                {
                    { "signature", sig },
                    { "enabled", hook.Enabled },
                    { "patchSource", hook.PatchSourceCode },
                });
            }
            return new Dictionary<string, object> { { "ok", true }, { "hooks", hooks } };
        }

        internal static object ToggleHook(string signature)
        {
            if (!(HookList.currentHooks[signature] is HookInstance hook))
                return Error($"No hook found for signature: {signature}");
            hook.TogglePatch();
            RefreshHookPanel();
            return new Dictionary<string, object> { { "ok", true }, { "enabled", hook.Enabled } };
        }

        internal static object DeleteHook(string signature)
        {
            if (!(HookList.currentHooks[signature] is HookInstance hook))
                return Error($"No hook found for signature: {signature}");
            hook.Unpatch();
            HookList.currentHooks.Remove(signature);
            HookList.hookedSignatures.Remove(signature);
            RefreshHookPanel();
            return new Dictionary<string, object> { { "ok", true } };
        }

        static void RefreshHookPanel()
        {
            if (HookList.HooksScrollPool != null)
                HookList.HooksScrollPool.Refresh(true, false);
        }

        #endregion


        #region Watch (per-frame expression sampling)

        // Called from the HTTP thread; compiles once, samples per frame in a coroutine.
        internal static object Watch(string expression, int frames, int timeoutMs)
        {
            frames = Math.Max(1, Math.Min(frames, 600));

            List<object> samples = new();
            Exception error = null;
            ManualResetEvent done = new(false);

            MainThreadDispatcher.Run(() =>
            {
                Mono.CSharp.CompiledMethod compiled = ConsoleController.Evaluator.Compile(expression);
                if (compiled == null)
                    throw new FormatException("Expression did not compile to a REPL expression (must be a single expression, no trailing ';').");
                RuntimeHelper.StartCoroutine(WatchCoroutine(compiled, frames, samples, e => { error = e; }, done));
                return null;
            }, timeoutMs);

            if (!done.WaitOne(timeoutMs, false))
                throw new TimeoutException($"Watch did not finish within {timeoutMs}ms.");
            if (error != null)
                throw new TargetInvocationException(error);

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "expression", expression },
                { "frames", samples.Count },
                { "samples", samples },
            };
        }

        static IEnumerator WatchCoroutine(Mono.CSharp.CompiledMethod compiled, int frames, List<object> samples, Action<Exception> onError, ManualResetEvent done)
        {
            for (int i = 0; i < frames; i++)
            {
                try
                {
                    object ret = null;
                    compiled.Invoke(ref ret);
                    samples.Add(new Dictionary<string, object>
                    {
                        { "frame", i },
                        { "time", Time.time },
                        { "value", ret?.ToString() ?? "null" },
                    });
                }
                catch (Exception ex)
                {
                    onError(ex);
                    done.Set();
                    yield break;
                }
                yield return null;
            }
            done.Set();
        }

        #endregion


        #region Screen raycast

        // x/y are normalized 0..1 from the TOP-LEFT (same orientation as screenshots).
        internal static object InspectAt(float x, float y)
        {
            Camera cam = Camera.main;
            if (!cam)
                return Error("No main camera found (Camera.main is null).");

            Vector3 screenPos = new(x * Screen.width, (1f - y) * Screen.height, 0f);
            Ray ray = cam.ScreenPointToRay(screenPos);

            if (!Physics.Raycast(ray, out RaycastHit hit, float.MaxValue))
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "hit", false },
                    { "note", "No collider under that point (world raycast only; UI elements are not hit)." },
                };

            GameObject go = hit.transform.gameObject;
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "hit", true },
                { "name", go.name },
                { "path", AIBridgeServer.GetGameObjectPath(go.transform) },
                { "id", go.GetInstanceID() },
                { "distance", hit.distance },
                { "components", go.GetComponents<Component>().Where(c => c != null).Select(c => (object)c.GetType().Name).ToList() },
            };
        }

        #endregion


        #region Freecam

        internal static object Freecam(bool enabled, float? posX, float? posY, float? posZ)
        {
            if (enabled)
            {
                if (!UI.Panels.FreeCamPanel.inFreeCamMode)
                    UI.Panels.FreeCamPanel.BeginFreecam();

                Camera cam = UI.Panels.FreeCamPanel.ourCamera;
                if (!cam)
                    return Error("Freecam camera unavailable after enabling.");

                if (posX.HasValue && posY.HasValue && posZ.HasValue)
                    cam.transform.position = new Vector3(posX.Value, posY.Value, posZ.Value);

                Vector3 position = cam.transform.position;
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "enabled", true },
                    { "position", position.ToString() },
                };
            }
            else
            {
                if (UI.Panels.FreeCamPanel.inFreeCamMode)
                    UI.Panels.FreeCamPanel.EndFreecam();
                return new Dictionary<string, object> { { "ok", true }, { "enabled", false } };
            }
        }

        #endregion


        static Dictionary<string, object> Error(string message)
            => new() { { "ok", false }, { "error", message } };
    }
}
#endif
