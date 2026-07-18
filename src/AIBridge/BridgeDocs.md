# UnityExplorer AI Bridge — API documentation

This game instance embeds a localhost HTTP JSON API for AI agents: inspect live
game state and execute C# inside the running game.

- All responses are JSON with an `ok` boolean; failures carry an `error` string.
- Requests execute on the Unity main thread (max ~1 per frame). Timeout after 15s
  (e.g. game paused/frozen) returns HTTP 500.
- `GET /` — machine-readable endpoint index (liveness check). `GET /docs` — this document.
- Two access modes, same underlying tools: plain REST endpoints (below, curl-friendly)
  and an MCP server at `POST /mcp` (JSON-RPC 2.0, Streamable HTTP transport, stateless).
  MCP registration:
  - Claude Code: `claude mcp add --transport http uch-game http://127.0.0.1:7311/mcp`
  - Codex (`~/.codex/config.toml`): `[mcp_servers.uch-game]` / `url = "http://127.0.0.1:7311/mcp"`
  - MCP tools: `get_scene`, `inspect_gameobject`, `inspect_type`, `execute_csharp`, `get_logs`
    (parameters mirror the REST endpoints below).

## Endpoints

### GET /scene — hierarchy overview
`curl "http://127.0.0.1:7311/scene?depth=2&max=500"`

- `depth` (default 3): child recursion depth below root objects.
- `max` (default 2000): node cap; `"truncated": true` when hit.

Returns per scene (incl. `DontDestroyOnLoad`): `rootObjects` tree of
`{ name, active, components: [type names], childCount, children? }`.

Strategy: start shallow (`depth=1`) to find interesting roots, then drill down
by inspecting specific paths. `childCount` greater than the number of returned
`children` means there is more below the depth limit.

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

Type name may be short or namespace-qualified (resolved across all loaded
assemblies, incl. Assembly-CSharp). Returns fields, properties, and method
signatures (static + instance, public + private) — no values.

### POST /execute — run C# in the game
Body = raw C# (no JSON wrapping). REPL semantics: a trailing expression
without `;` is returned as `result`.

```
curl -X POST --data 'UnityEngine.Time.timeScale' http://127.0.0.1:7311/execute
# {"ok":true,"result":"1","error":null}

curl -X POST --data 'Time.timeScale = 0.5f;' http://127.0.0.1:7311/execute
```

- Default usings: System, System.Linq, System.Text, System.Collections(.Generic),
  System.Reflection, UnityEngine, UniverseLib. Game types (Assembly-CSharp) are
  referenced and usable directly by name.
- State persists between calls (variables, defined classes) until the C#
  console is reset.
- `ok:false` + `error` carries compiler errors or the thrown exception.
- Multi-line scripts are fine; send a file as body:
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
