# AI Bridge — usage guide (for AI agents)

> **Note:** The full API documentation is also served by the running game itself at
> `GET http://127.0.0.1:7311/docs` (embedded in the DLL from `src/AIBridge/BridgeDocs.md`,
> so it always matches the deployed version — prefer that over this file for endpoint
> details). This file additionally covers build/deploy.

UnityExplorer (this fork) embeds a localhost HTTP JSON API in the running game.
Use it to inspect live game state and execute C# inside Ultimate Chicken Horse
while the game is running.

- Base URL: `http://127.0.0.1:7311` (port = "AI Bridge Port" in `BepInEx\config\com.sinai.unityexplorer.cfg`, `0` = disabled)
- All responses are JSON with an `ok` boolean; failures carry an `error` string.
- Requests execute on the Unity main thread (max ~1 per frame). Timeout after 15s
  (e.g. game paused/frozen) returns HTTP 500.
- `GET /` lists all endpoints (liveness check).

## Endpoints

### GET /scene — hierarchy overview
`curl "http://127.0.0.1:7311/scene?depth=2&max=500"`

- `depth` (default 3): child recursion depth below root objects.
- `max` (default 2000): node cap; `"truncated": true` when hit.

Returns per scene (incl. `DontDestroyOnLoad`): `rootObjects` tree of
`{ name, active, components: [type names], childCount, children? }`.

Strategy: start shallow (`depth=1`) to find interesting roots, then drill down
by inspecting specific paths. `childCount` > number of returned `children`
means there is more below the depth limit.

### GET /inspect?path=... — live GameObject state
`curl "http://127.0.0.1:7311/inspect?path=MainMenu_Artwork/Horse"`

- `path` is the slash-joined hierarchy path (root object name first), as
  reconstructable from `/scene`. Finds inactive children too; searches all
  loaded scenes + DontDestroyOnLoad.

Returns `{ name, path, active, layer, tag, components: [{ type, members }] }`
where members are all instance fields+properties with current values
(`value` is `ToString()`, truncated at 500 chars).

### GET /inspect?type=... — type signatures
`curl "http://127.0.0.1:7311/inspect?type=PlayerCharacterController"`

Type name may be short or namespace-qualified (resolved via UniverseLib across
all loaded assemblies, incl. Assembly-CSharp). Returns fields, properties, and
method signatures (static + instance, public + private) — no values.

For deeper static analysis, prefer reading the decompiled game source /
`Assembly-CSharp.dll` in the workspace; use this endpoint for quick lookups.

### POST /execute — run C# in the game
Body = raw C# (no JSON wrapping). REPL semantics: a trailing expression
without `;` is returned as `result`.

```
curl -X POST --data 'UnityEngine.Time.timeScale' http://127.0.0.1:7311/execute
# {"ok":true,"result":"1","error":null}

curl -X POST --data 'Time.timeScale = 0.5f;' http://127.0.0.1:7311/execute
```

- Default usings: System, System.Linq, System.Text, System.Collections(.Generic),
  System.Reflection, UnityEngine, UniverseLib. Assembly-CSharp types are
  referenced and usable directly by name.
- State persists between calls (variables, defined classes) until the C#
  console is reset.
- `ok:false` + `error` carries compiler errors or the thrown exception.
- Multi-line scripts are fine; send the file as body:
  `curl -X POST --data-binary @script.cs http://127.0.0.1:7311/execute`

Useful patterns:
```csharp
// find objects
var all = UnityEngine.Object.FindObjectsOfType<PlayerCharacterController>();
all.Length

// dump something complex as a string result
string.Join("\n", all.Select(p => p.name).ToArray())

// Harmony patching works (HarmonyLib is loaded); keep patch classes unique per session
```

### GET /logs?since=N — UnityExplorer + Unity log tail
`curl "http://127.0.0.1:7311/logs?since=120"`

Returns `{ total, entries: [{ index, type, message }] }` from index `since`.
Poll by passing the previous `total` as `since` (cursor). Unity `Debug.Log`
messages only appear if the "Log Unity Debug" config option is enabled.
`Debug.Log` from your own `/execute` payloads is a good feedback channel.

## Typical debugging loop

1. `GET /` — confirm the game is up.
2. `GET /scene?depth=1` — orient.
3. `GET /inspect?path=...` / `?type=...` — narrow down state and API.
4. `POST /execute` — probe values, then mutate / patch.
5. `GET /logs?since=<cursor>` — observe effects, exceptions, own Debug.Log output.

## Setup recap

- Build single-DLL output: `.\build_uch.ps1` (builds BIE5_Mono, then ILRepack-merges
  UniverseLib + mcs + Tomlet into `Release\UnityExplorer.BepInEx5.Mono\UnityExplorer.BIE5.Mono.dll`)
- Deploy that one DLL to
  `S:\SteamLibrary\steamapps\common\Ultimate Chicken Horse\BepInEx\plugins\`
- Launch the game; bridge logs `AI Bridge listening on http://127.0.0.1:7311/`.
- API docs for the deployed version: `curl http://127.0.0.1:7311/docs`

## MCP mode

The same five tools are exposed as an MCP server (Streamable HTTP, stateless) at
`POST /mcp` — usable directly by Claude Code, Codex, Cursor, etc., no bridge process:

- Claude Code: `claude mcp add --transport http uch-game http://127.0.0.1:7311/mcp`
- Codex (`~/.codex/config.toml` or project `.codex/config.toml`):
  ```toml
  [mcp_servers.uch-game]
  url = "http://127.0.0.1:7311/mcp"
  ```

REST endpoints remain available alongside; both call the same handlers.

### Always-on proxy (recommended registration target)

`mcp-proxy/` is a tiny .NET console app that hides game downtime from MCP clients
(clients only probe MCP servers at session start, so registering the game directly
means no tools when it isn't running yet):

- Listens on `http://127.0.0.1:7312/mcp`, forwards to the game on 7311.
- Game down: answers `initialize`/`ping` itself, serves `tools/list` from a cache
  (`tools-cache.json`, filled whenever the game was reachable), and returns a clean
  "game is not running, ask the user to launch UCH" tool error on `tools/call`.
- Game restarts are invisible (everything is stateless) — calls just work again.

Run it: `dotnet run --project mcp-proxy -c Release` (keep it running, e.g. autostart).
Register clients against **7312** instead of 7311:

- Claude Code: `claude mcp add --transport http uch-game http://127.0.0.1:7312/mcp`
- Codex: `url = "http://127.0.0.1:7312/mcp"`

### Multi-instance (networked mods)

Game instances auto-bind ports 7311, 7313, 7314... (up to 16 slots, 7312 = proxy).
The proxy (v2) scans the range and adds:

- `list_instances` — running instances with identity (port, pid, steamName, utc clock)
- `_port` argument injected into every game tool — target a specific instance
- `launch_game(count)` / `kill_game(pid?)` — full lifecycle control from the agent
  (game exe path via `UCH_DIR` env var, default `S:\SteamLibrary\...\Ultimate Chicken Horse`)
- `postmortem(lines)` — disk log tails (UnityExplorer / BepInEx / Unity player),
  readable even when the game crashed or hung

Logs and observer events carry wall-clock `utc` fields for cross-instance correlation.
