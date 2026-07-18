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
  - MCP tools: `get_scene`, `inspect_gameobject`, `inspect_type`, `inspect_id`, `find_objects`,
    `execute_csharp`, `create_hook`, `list_hooks`, `toggle_hook`, `delete_hook`, `watch`,
    `create_observer`, `list_observers`, `delete_observer`, `get_events`, `wait_for`,
    `inspect_at`, `freecam`, `screenshot`, `export_texture`, `replace_texture`, `export_sprites`,
    `get_logs` (parameters mirror the REST endpoints below).

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

### GET /search — find objects, singletons, classes
`curl "http://127.0.0.1:7311/search?mode=singleton&name=Manager&limit=20"`

- `mode=object` (default): live UnityEngine.Objects, filter by `name` substring and/or `type`.
  Results include `path` and instance `id`.
- `mode=singleton`: classes with a live static Instance field — the fastest way to find
  game managers/entry points.
- `mode=class`: type names across all loaded assemblies.

### GET /inspect?id=N — inspect by instance id
Every scene node, search result and component now carries a Unity instance `id`.
`?id=` inspects any UnityEngine.Object unambiguously (paths break on duplicate names).

### Hooks — Harmony method patching
```
curl "http://127.0.0.1:7311/hooks"                          # list
curl -X POST --data '{"type":"PlayerCharacterController","method":"Jump"}' http://127.0.0.1:7311/hooks/create
curl "http://127.0.0.1:7311/hooks/toggle?sig=<signature>"   # enable/disable
curl "http://127.0.0.1:7311/hooks/delete?sig=<signature>"   # remove
```
Without `patch_code`, a default Postfix is generated that logs every call (instance,
args, return value) to the log — instant call tracing, read it via `/logs`.
With `patch_code`, supply C# method(s) named `Prefix` (bool or void), `Postfix`,
`Finalizer` and/or `Transpiler` to change behavior. `param_types` (array of parameter
type names) disambiguates overloads. Hooks persist until deleted and also appear in
UnityExplorer's Hooks panel.

### POST /watch — per-frame expression sampling
`curl -X POST --data 'Time.timeScale' "http://127.0.0.1:7311/watch?frames=120"`

Body = single C# expression (no trailing `;`). Compiled once, evaluated every frame
for N frames (default 60, max 600); returns `{frame, time, value}` samples.

### Observers — "notify me when this changes"
```
curl -X POST --data 'PlayerManager.Instance.playerCount' "http://127.0.0.1:7311/observers/create?mode=change"
curl "http://127.0.0.1:7311/events?since=0"      # drain recorded events (cursor = previous 'total')
curl "http://127.0.0.1:7311/observers"           # list active observers
curl "http://127.0.0.1:7311/observers/delete?id=1"
```
Observers sample the expression **every frame** and record changes (`mode=change`)
or false→true edges (`mode=true`) into an event buffer — nothing is missed while
you work on other things; drain with `/events` when convenient. Events carry
observerId, kind (change/condition/error), value, previous, time, frame.
A throwing expression records one `error` event and deactivates the observer.
Observers and events do not survive a game restart.

### POST /wait_for — block until something happens
`curl -X POST --data 'PlayerManager.Instance.playerCount > 1' "http://127.0.0.1:7311/wait_for?timeout=60000"`

Blocks until the expression becomes true (`mode=true`, default) or changes
(`mode=change`), then returns the triggering value and frames waited — the
closest thing to a push notification in a request/response flow. Returns
`triggered:false` on timeout (default 30s, cap 5min). Keep the timeout within
your MCP client's tool timeout.

### GET /inspect_at — what's at this pixel?
`curl "http://127.0.0.1:7311/inspect_at?x=0.5&y=0.5"`

World Physics.Raycast at normalized screen coordinates, 0..1 from the **top-left**
(same orientation as screenshots, resolution-independent). Returns the hit
GameObject's name/path/id/components. Colliders only — UI elements are not hit.

### GET /freecam — camera control
`curl "http://127.0.0.1:7311/freecam?enabled=true&x=10&y=5&z=-20"`

Enables UnityExplorer's free camera (optionally at a world position) — useful for
framing screenshots of specific areas. `enabled=false` restores the game camera.

### GET /screenshot — see the game
`curl -o shot.png "http://127.0.0.1:7311/screenshot?max=1280"`

Returns a PNG of the game window, captured after rendering. `max` = optional
maximum width/height (image is scaled down to fit); omit or 0 for full
resolution. Also available as the MCP tool `screenshot` (returns inline image
content, default max 1280).

### Textures & sprite sheets — export, edit, replace
```
curl -o sheet.png "http://127.0.0.1:7311/texture?id=-1234"          # export as PNG
curl "http://127.0.0.1:7311/sprites?texture_id=-1234"               # sprite manifest (add &png=1 for base64 PNG inline)
curl -X POST --data-binary @sheet.png "http://127.0.0.1:7311/texture/replace?id=-1234"
```
- `/texture?id=` exports any Texture2D as PNG. The id may also be a Sprite,
  Material, SpriteRenderer or UI.Image — the underlying texture is resolved.
  Non-CPU-readable game textures are handled via a GPU blit.
- `/sprites?texture_id=` dumps a manifest of every Sprite on that sheet: name, id,
  `rect` (Unity convention, origin **bottom-left**), `pngRect` (origin **top-left** —
  directly usable as PNG pixel coordinates), pivot (pixels + normalized),
  `pixelsPerUnit` and 9-slice `border`. Everything needed to edit a sheet and put
  each frame back in the right place.
- `POST /texture/replace?id=` loads PNG bytes into the **existing** Texture2D
  instance (LoadImage), so every renderer, material, sprite and prefab clone that
  references it updates immediately — animation and atlas rects stay intact if the
  replacement keeps the original dimensions. In-memory only; lost on restart
  (re-apply from `Scripts\startup.cs` to persist).
- All data travels over HTTP (raw PNG on REST, base64 on the MCP tools), so the
  agent does not need filesystem access to the game machine.

Typical reskin loop: `find_objects`/`inspect` to get the sheet texture id →
`/sprites?texture_id=` for manifest + PNG → edit locally → `POST /texture/replace`
→ `/screenshot` to verify. Note: character outfits re-apply sprites every frame
from the sheet, which is exactly why replacing the *texture* works while swapping
individual `sprite` references gets reverted.

### GET /logs?since=N — UnityExplorer + Unity log tail
`curl "http://127.0.0.1:7311/logs?since=120"`

Returns `{ total, entries: [{ index, type, message }] }` from index `since`.
Poll by passing the previous `total` as `since` (cursor). Unity `Debug.Log`
messages only appear if the "Log Unity Debug" config option is enabled.
`Debug.Log` from your own `/execute` payloads is a good feedback channel.

## Multiple instances (networked-mod testing)

Each game instance binds the first free port in `[7311, 7343)` — host on 7311,
clients on 7312, 7313, ... — supporting up to 32 concurrent instances. The proxy
sits just below the range on 7310.
`GET /` includes an `instance` identity: port, pid, steamName, and a wall-clock `utc`.
Log entries and observer events also carry `utc` timestamps so you can correlate
"host sent X" / "client received X" across instances.

Via the proxy (port 7310), use `list_instances` to enumerate running instances and
pass `_port` on any game tool to target a specific one. `launch_game(count)`,
`kill_game` and `postmortem` (disk log tails, works when the game is dead) are
served by the proxy itself. `launch_game` refuses to start if the mod DLL is
missing from `BepInEx\plugins`, BepInEx's doorstop is absent/disabled, or the
bridge port is configured to 0 — and warns when the deployed DLL is older than
the repo's last build (forgot to redeploy). Its optional `args` string array is passed
unchanged to every launched game process. For example:
`launch_game {count: 1, args: ["-screen-width", "1280", "-screen-height", "720"]}`.
Kill all instances before redeploying the mod DLL —
the file is locked while any instance runs.

**Restart resilience:** hooks and observers are in-memory and lost on restart.
To persist setup across restarts, write it as C# to
`<game>\BepInEx\plugins\zmarn-dev-UltimateGlorpExplorer\Scripts\startup.cs` — UltimateGlorpExplorer
executes it on every boot.

## Typical debugging loop

1. `GET /` — confirm the game is up.
2. `GET /scene?depth=1` — orient.
3. `GET /inspect?path=...` / `?type=...` — narrow down state and API.
4. `POST /execute` — probe values, then mutate / patch.
5. `GET /logs?since=<cursor>` — observe effects, exceptions, own Debug.Log output.
