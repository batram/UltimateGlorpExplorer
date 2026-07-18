# The AI Bridge

UltimateGlorpExplorer embeds an HTTP + MCP server inside the running game so AI agents
(Claude Code, Codex, Cursor, ...) can explore, debug and script Ultimate Chicken Horse live.

The authoritative runtime reference is served by the game itself at
`GET http://127.0.0.1:7311/docs` (embedded from [src/AIBridge/BridgeDocs.md](../src/AIBridge/BridgeDocs.md),
always matches the deployed DLL). This file is the human-facing companion: setup,
architecture, and worked examples.

## Setup

```powershell
.\build_uch.ps1        # builds BIE5_Mono and ILRepack-merges everything into ONE dll
# copy Release\UnityExplorer.BepInEx5.Mono\UnityExplorer.BIE5.Mono.dll to <game>\BepInEx\plugins\

dotnet run --project mcp-proxy -c Release    # keep the always-on proxy running (port 7310)
```

Register your agent against the **proxy** (survives game restarts, can launch the game itself):

- Claude Code: `claude mcp add --transport http uch-game http://127.0.0.1:7310/mcp`
- Codex (`~/.codex/config.toml`):
  ```toml
  [mcp_servers.uch-game]
  url = "http://127.0.0.1:7310/mcp"
  ```

Direct REST access (any tool, curl, no MCP needed) is always available on the game's own
port: `curl http://127.0.0.1:7311/` lists endpoints, `/docs` the full reference.

## Architecture

```
Agent (MCP or curl)
   │
   ├── mcp-proxy  :7310   always-on; hides game downtime, launches/kills the game,
   │        │             routes _port to instances, serves postmortem from disk
   │        ▼
   └── AIBridgeServer  :7311, 7313, 7314, ...   (one per game instance, first free port)
            │  HttpListener bg thread → MainThreadDispatcher (drained each frame)
            ├─ SceneHandler / SearchProvider     (explore & search)
            ├─ ReflectionUtility                 (inspect)
            ├─ ConsoleController (mcs REPL)      (execute, watch, observers, wait_for)
            ├─ HookInstance (HarmonyX)           (method hooks)
            └─ LogPanel / TextureHelper          (logs, screenshots)
```

Everything is stateless JSON over HTTP; a game restart is invisible to registered clients.
Config: "AI Bridge Port" in `BepInEx\config\com.sinai.unityexplorer.cfg` (0 disables).

## Tool overview

| Tool (MCP) | REST | What it does |
|---|---|---|
| `get_scene` | `GET /scene?depth&max` | Scene hierarchy incl. DontDestroyOnLoad + HideAndDontSave, with instance ids |
| `find_objects` | `GET /search?mode&name&type&limit` | UnityObject / **singleton** / class search |
| `inspect_gameobject` | `GET /inspect?path=` | Component fields/properties with live values |
| `inspect_id` | `GET /inspect?id=` | Inspect any UnityEngine.Object by instance id |
| `inspect_type` | `GET /inspect?type=` | Member signatures of a type |
| `execute_csharp` | `POST /execute` | Run C# in the game (persistent REPL) |
| `create_hook` / `list_hooks` / `toggle_hook` / `delete_hook` | `/hooks*` | Harmony hooks; default patch = call tracing |
| `watch` | `POST /watch?frames` | Sample an expression once per frame, return series |
| `create_observer` / `get_events` / ... | `/observers*`, `/events` | Per-frame observers recording into an event buffer |
| `wait_for` | `POST /wait_for` | Block until an expression triggers (pseudo-notification) |
| `inspect_at` | `GET /inspect_at?x&y` | World raycast: "what's at this pixel?" |
| `screenshot` | `GET /screenshot?max` | PNG of the game (MCP returns inline image) |
| `freecam` | `GET /freecam` | Toggle/position the free camera |
| `export_texture` | `GET /texture?id` | Export any texture as PNG (id may be a Sprite/Material/renderer) |
| `replace_texture` | `POST /texture/replace?id` | Load PNG into the live Texture2D in place — instant reskin |
| `export_sprites` | `GET /sprites?texture_id` | Sprite-sheet manifest: name, rect (+ PNG-space rect), pivot, PPU, border |
| `get_logs` | `GET /logs?since` | Log tail with cursor + utc timestamps |
| *proxy:* `list_instances`, `launch_game`, `kill_game`, `postmortem` | — | Lifecycle & crash forensics, work with zero instances |

`launch_game` sanity-checks the mod setup before starting: it refuses to launch if the
BepInEx doorstop (`winhttp.dll`) is missing/disabled, the UnityExplorer DLL is absent from
`BepInEx\plugins`, or the AI bridge port is set to 0 in the config — and it warns (but still
launches) when the deployed DLL is older than the repo's last build, i.e. you forgot to redeploy.

## Worked examples

### Orient, identify, mutate

```bash
curl "http://127.0.0.1:7311/scene?depth=1"                       # what's here?
curl "http://127.0.0.1:7311/search?mode=singleton&name=Manager"  # find the managers
curl "http://127.0.0.1:7311/inspect?path=MainMenu_Artwork/Horse" # live values
curl -X POST --data 'Time.timeScale = 0.5f;' http://127.0.0.1:7311/execute
```

### See the game, ask what's on screen

```bash
curl -o shot.png "http://127.0.0.1:7311/screenshot?max=1280"
curl "http://127.0.0.1:7311/inspect_at?x=0.5&y=0.5"   # normalized, top-left origin
```

### Trace who calls what (hooks)

```bash
# default hook = log every call with args + return value
curl -X POST --data '{"type":"PlayerCharacterController","method":"Jump"}' \
     http://127.0.0.1:7311/hooks/create
curl "http://127.0.0.1:7311/logs?since=0"    # watch the calls come in

# custom behavior patch
curl -X POST --data '{"type":"PlayerCharacterController","method":"Jump",
  "patch_code":"static void Prefix(PlayerCharacterController __instance) { UnityEngine.Debug.Log(\"jump!\"); }"}' \
  http://127.0.0.1:7311/hooks/create
```

### Observe without missing anything

```bash
curl -X POST --data 'PlayerManager.Instance.playerCount' \
     "http://127.0.0.1:7311/observers/create?mode=change"
# ... play / do other work ...
curl "http://127.0.0.1:7311/events?since=0"           # every change, with frame + utc

# blocking variant: "tell me when the round starts"
curl -X POST --data 'GameControl.Instance != null' \
     "http://127.0.0.1:7311/wait_for?timeout=60000"
```

### Reskin a sprite sheet (texture pipeline)

```bash
# 1. find the sheet: any Sprite/Material/SpriteRenderer id resolves to its texture
curl "http://127.0.0.1:7311/search?name=blockDirt&type=Sprite"

# 2. export the PNG + a manifest of every sprite on the sheet
curl -o sheet.png "http://127.0.0.1:7311/texture?id=-4242"
curl "http://127.0.0.1:7311/sprites?texture_id=-4242"
#   manifest gives pngRect (top-left origin) per sprite = exact PNG pixel coords to edit

# 3. edit sheet.png locally, then push it back into the live texture
curl -X POST --data-binary @sheet.png "http://127.0.0.1:7311/texture/replace?id=-4242"

# 4. verify
curl -o after.png "http://127.0.0.1:7311/screenshot?max=1280"
```

Replacing the *texture* (not individual `sprite` references) is the key: systems that
re-apply sprites every frame (e.g. character outfits) keep pointing at the same
Texture2D instance, so the new pixels show up everywhere immediately with animation
intact. The MCP tools (`export_texture`/`replace_texture`/`export_sprites`) carry the
PNG as base64 inside the response, so a remote/tunneled agent needs no file access to
the game machine. Changes are in-memory; persist via `Scripts\startup.cs` (below).

### Networked-mod loop (multiple instances)

Via MCP against the proxy:

1. `launch_game {count: 2}` → host + client come up on ports 7311 and 7313
2. `list_instances` → identities (port, pid, steamName, utc clock)
3. Target instances per call: `execute_csharp {_port: 7311, code: "..."}` vs `{_port: 7313, ...}`
4. Hook the send path on the host, the receive path on the client; correlate via the
   `utc` timestamps on both instances' logs/events
5. Game crashed? `postmortem` reads the UnityExplorer / BepInEx / Unity player logs from disk
6. `kill_game`, redeploy the mod DLL, `launch_game` again — the whole loop is agent-drivable

### Restart resilience

Hooks and observers are in-memory. Persist agent setup by writing C# to
`<game>\BepInEx\plugins\sinai-dev-UnityExplorer\Scripts\startup.cs` — it runs on every boot.

## Repo layout

- [src/AIBridge/](../src/AIBridge/) — everything bridge (`#if MONO` only): server, MCP endpoint,
  dispatcher, tools, observers, embedded docs
- [mcp-proxy/](../mcp-proxy/) — the always-on proxy (net6 console app, no dependencies)
- [build_uch.ps1](../build_uch.ps1) — single-DLL build (ILRepack: UniverseLib + mcs + Tomlet + Newtonsoft.Json)
