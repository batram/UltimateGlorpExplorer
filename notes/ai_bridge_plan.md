# AI Bridge for UnityExplorer (UCH / Mono only)

HTTP JSON API embedded in UnityExplorer so an external AI agent (Claude Code / codex)
can inspect and script a running UCH instance. Target: **BIE5_Mono only** (.NET 3.5,
BepInEx 5). Other build configs are out of scope.

## Architecture

```
Claude Code (curl / optional stdio MCP bridge)
        │  HTTP JSON, localhost
        ▼
AIBridgeServer (HttpListener, background thread)
        │  request queue
        ▼
MainThreadDispatcher  ← drained from ExplorerCore.Update()
        │
        ├─ SceneHandler          (scene hierarchy)
        ├─ ReflectionUtility     (inspect, UniverseLib)
        ├─ ConsoleController     (C# REPL eval)
        └─ LogPanel              (log access)
```

## New code: `src/AIBridge/`

### AIBridgeServer.cs
- `System.Net.HttpListener` on `http://127.0.0.1:<port>/` (net35-safe, no extra deps).
- Port + enable flag via `ConfigManager` (default off, or on with fixed port — decide at impl).
- Background thread accepts requests, enqueues work item, blocks (with timeout ~5s)
  until main thread completes, writes JSON response.
- Start from end of `ExplorerCore.LateInit()` (src/ExplorerCore.cs).

### MainThreadDispatcher.cs
- Queue of pending requests (lock + Queue<T>, net35 — no ConcurrentQueue).
- Drained in `ExplorerCore.Update()` (hook via ExplorerBehaviour update path).
- Each item: delegate returning object, ManualResetEvent to signal HTTP thread.

### Json.cs
- Minimal hand-rolled JSON writer (+ tiny parser for POST bodies). No Newtonsoft.

## Endpoints (v1)

- `GET /scene?depth=N&max=N`
  - `SceneHandler.LoadedScenes` / `CurrentRootObjects`, recursive transform walk.
  - Per node: name, active, path, component type names, childCount. Depth/count
    limits mandatory (UCH levels can be large).
- `GET /inspect?path=<scene-path>` or `?type=<TypeName>`
  - Resolve GameObject by hierarchy path or type via UniverseLib `ReflectionUtility`.
  - Return fields/properties (name, type, value.ToString()), method signatures.
  - Do NOT use InspectorManager (UI-coupled).
- `POST /execute` (body = raw C# string)
  - New `ConsoleController.EvaluateCapture(string)` variant based on existing
    `Evaluate(string, bool)` (src/CSConsole/ConsoleController.cs:158):
    capture return value + compiler errors via ScriptEvaluator's StringWriter /
    report printer instead of ExplorerCore.Log. Return `{ ok, result, errors }`.
- `GET /logs?since=N`
  - Add public accessor for `LogPanel.Logs` (src/UI/Panels/LogPanel.cs, currently
    private). Return entries with index, type, message; `since` = index cursor.

## Client side

- Claude Code calls endpoints via curl; document examples in this folder.
- Copy `UltimateChickenHorse_Data/Managed/Assembly-CSharp.dll` into the agent
  workspace for static code context.
- Optional later: thin Node/Python stdio→HTTP MCP bridge exposing the 4 endpoints
  as MCP tools.

## Verification

1. Build BIE5_Mono config, drop DLL into UCH `BepInEx/plugins/`, launch UCH.
2. `curl http://127.0.0.1:<port>/scene` → hierarchy JSON.
3. `curl -X POST --data 'UnityEngine.Debug.Log("hi");' .../execute` → ok, and
   "hi" visible in `/logs`.
4. Game stays responsive (one request handled per frame is acceptable).
