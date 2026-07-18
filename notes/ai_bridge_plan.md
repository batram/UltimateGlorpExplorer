# AI Bridge architecture (UCH / Mono only) — as built

HTTP JSON API + MCP server embedded in UnityExplorer so external AI agents
(Claude Code / Codex) can inspect and script a running UCH instance.
Target: **BIE5_Mono only** (.NET 3.5, BepInEx 5). All bridge code is `#if MONO`.

## Architecture

```
Claude Code / Codex ──MCP (Streamable HTTP)──► mcp-proxy (:7312, always on)
        │                                          │ forwards; hides game downtime
        │ curl (REST)                              ▼
        └────────────────────────────► AIBridgeServer (:7311, HttpListener,
                                       background thread, in-game)
                                                │  request queue
                                                ▼
                                     MainThreadDispatcher ◄─ drained from
                                                │            ExplorerCore.Update()
                                                ├─ SceneHandler / scene walk
                                                ├─ ReflectionUtility (UniverseLib)
                                                ├─ ConsoleController.EvaluateCapture
                                                └─ LogPanel accessors
```

## Components (`src/AIBridge/`)

- **AIBridgeServer.cs** — `HttpListener` on `http://127.0.0.1:<port>/`
  (config "AI Bridge Port", default 7311, 0 = disabled), started from
  `ExplorerCore.LateInit()`. Routes:
  - `GET /` endpoint index · `GET /docs` embedded BridgeDocs.md (markdown)
  - `GET /scene?depth&max` · `GET /inspect?path=|type=` · `POST /execute` · `GET /logs?since`
  - `POST /mcp` → McpEndpoint
  - Shared internal handlers (BuildSceneTree, InspectGameObject, InspectType,
    ExecuteCode, GetLogs) are used by both REST and MCP.
- **McpEndpoint.cs** — minimal MCP server, Streamable HTTP transport
  (JSON-RPC 2.0 on POST, stateless, no SSE): initialize / notifications (202) /
  ping / tools/list / tools/call. Five tools mirroring the REST endpoints:
  get_scene, inspect_gameobject, inspect_type, execute_csharp, get_logs.
- **MainThreadDispatcher.cs** — lock+Queue (net35), drained once per frame in
  `ExplorerCore.Update()`; HTTP thread blocks with 15s timeout.
- **BridgeDocs.md** — agent-facing API docs, embedded resource, served at /docs.
- JSON: **Newtonsoft.Json 13.0.3** (net35 build) for both directions
  (the initial hand-rolled writer/parser was replaced).

## Hooks into existing code

- `ConsoleController.EvaluateCapture(string)` (src/CSConsole/ConsoleController.cs) —
  REPL eval returning result/compiler errors instead of logging.
- `LogPanel.LogCount` / `GetLog(int)` (src/UI/Panels/LogPanel.cs) — read access.
- `ConfigManager.AI_Bridge_Port` (src/Config/ConfigManager.cs).
- `ExplorerCore.LateInit()` starts the server; `ExplorerCore.Update()` drains the
  dispatcher (src/ExplorerCore.cs).

## mcp-proxy/ (separate always-on process)

.NET 6 console app, `http://127.0.0.1:7312/mcp` → forwards to 7311. When the game
is down: answers initialize/ping itself, serves tools/list from `tools-cache.json`
(captured while the game was up), returns clean "game not running" tool errors on
tools/call. Stateless end-to-end, so game restarts are invisible to MCP clients.
Register clients against 7312. Run: `dotnet run --project mcp-proxy -c Release`.

## Build & deploy

- `.\build_uch.ps1` — builds BIE5_Mono, ILRepack-merges UniverseLib + mcs +
  Tomlet + Newtonsoft.Json into a single `UnityExplorer.BIE5.Mono.dll`.
- Deploy that one DLL to `<UCH>\BepInEx\plugins\` (close the game first — the
  file is locked while it runs).

## Verification (what was tested live)

- REST: /scene, /inspect (path+type), /execute (REPL result round-trip),
  /logs cursor, /docs.
- MCP direct + via proxy: initialize handshake, tools/list, tools/call
  (execute_csharp, get_scene), -32602 on unknown tool, notifications → 202.
- Proxy downtime cycle: cache fill while up → game quit → cached tools/list +
  friendly tool errors → relaunch → passthrough resumes.
