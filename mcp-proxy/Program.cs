using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

// Always-on MCP proxy for the UCH AI bridge, multi-instance aware.
//
// Clients register http://127.0.0.1:7310/mcp (just below the game port range, so
// instances number contiguously from 7311). Game instances bind the first free
// port in [BASE_UPSTREAM, BASE_UPSTREAM+32); the proxy scans that range and routes
// each tools/call to a specific instance via an injected optional "_port" argument.
// The proxy also answers seven tools itself (they work with zero instances running):
//   list_instances, launch_game, kill_game, postmortem, save_skill, list_skills, get_skill
//
// Skill library: agents can save reusable C# snippets as human-readable markdown
// files in skills/ next to the proxy exe — either explicitly via save_skill, or by
// passing a title (plus optional tags/comment) to execute_csharp, in which case the
// proxy strips those fields, forwards the code, and saves it only if the run succeeded.

const int ListenPort = 7310;
const int UpstreamBasePort = 7311;
const int UpstreamPortRange = 32;

string gameDir = Environment.GetEnvironmentVariable("UCH_DIR")
    ?? @"S:\SteamLibrary\steamapps\common\Ultimate Chicken Horse";
string gameExe = Path.Combine(gameDir, "UltimateChickenHorse.exe");
string cachePath = Path.Combine(AppContext.BaseDirectory, "tools-cache.json");
string skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
string failuresLogPath = Path.Combine(AppContext.BaseDirectory, "csharp-failures.jsonl");
object skillWriteLock = new();

HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
HttpClient probe = new() { Timeout = TimeSpan.FromMilliseconds(800) };

List<(int Port, JsonNode Identity)> aliveCache = new();
DateTime aliveCacheTime = DateTime.MinValue;

HttpListener listener = new();
listener.Prefixes.Add($"http://127.0.0.1:{ListenPort}/");
listener.Start();
Console.WriteLine($"uch-mcp-proxy: listening on http://127.0.0.1:{ListenPort}/mcp");
Console.WriteLine($"uch-mcp-proxy: scanning upstreams {UpstreamBasePort}-{UpstreamBasePort + UpstreamPortRange - 1}; game dir: {gameDir}");

while (true)
{
    HttpListenerContext ctx = await listener.GetContextAsync();
    _ = Task.Run(() => HandleAsync(ctx));
}

async Task HandleAsync(HttpListenerContext ctx)
{
    try
    {
        if (ctx.Request.Url.AbsolutePath.TrimEnd('/') != "/mcp" || ctx.Request.HttpMethod != "POST")
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        string body;
        using (StreamReader reader = new(ctx.Request.InputStream, Encoding.UTF8))
            body = await reader.ReadToEndAsync();

        string method = null;
        JsonNode id = null;
        JsonNode req = null;
        try
        {
            req = JsonNode.Parse(body);
            method = req?["method"]?.GetValue<string>();
            id = req?["id"];
        }
        catch { }

        switch (method)
        {
            case "tools/call":
                await HandleToolCallAsync(ctx, req, id, body);
                return;

            case "tools/list":
                await HandleToolsListAsync(ctx, id, body);
                return;

            case "initialize":
                {
                    // Forward to any alive instance; otherwise answer ourselves.
                    List<(int Port, JsonNode Identity)> alive = await GetAliveInstancesAsync();
                    if (alive.Count > 0 && await TryForwardAsync(ctx, alive[0].Port, body))
                        return;
                    string protocol = "2025-06-18";
                    try { protocol = req?["params"]?["protocolVersion"]?.GetValue<string>() ?? protocol; } catch { }
                    await RespondAsync(ctx, 200, RpcResult(id, new JsonObject
                    {
                        ["protocolVersion"] = protocol,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "uch-ai-bridge-proxy", ["version"] = "2.0" },
                        ["instructions"] = "Tools for inspecting and scripting live Ultimate Chicken Horse game instances. " +
                            "No instance is currently running — use launch_game to start one (or more, for networked testing), " +
                            "list_instances to see what's up, and pass _port on any game tool to target a specific instance. " +
                        // Only seen when no instance is alive at connect time — otherwise initialize is
                        // forwarded upstream and the game's instructions win (same as the _port guidance).
                        "A skill library of reusable C# snippets is available: list_skills / get_skill to reuse, " +
                        "save_skill (or execute_csharp with a title) to save.",
                    }).ToJsonString());
                    return;
                }

            case "ping":
                await RespondAsync(ctx, 200, RpcResult(id, new JsonObject()).ToJsonString());
                return;

            default:
                if (method != null && method.StartsWith("notifications/"))
                {
                    ctx.Response.StatusCode = 202;
                    ctx.Response.Close();
                    return;
                }
                {
                    List<(int Port, JsonNode Identity)> alive = await GetAliveInstancesAsync();
                    if (alive.Count > 0 && await TryForwardAsync(ctx, alive[0].Port, body))
                        return;
                    await RespondAsync(ctx, 200, RpcError(id, -32601, $"No game instance running; method not handled by proxy: {method}").ToJsonString());
                    return;
                }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"uch-mcp-proxy: error: {ex.Message}");
        try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
    }
}

async Task HandleToolCallAsync(HttpListenerContext ctx, JsonNode req, JsonNode id, string originalBody)
{
    string toolName = req?["params"]?["name"]?.GetValue<string>();
    JsonObject args = req?["params"]?["arguments"] as JsonObject ?? new JsonObject();

    // Proxy-local tools
    switch (toolName)
    {
        case "list_instances":
            {
                List<(int Port, JsonNode Identity)> alive = await GetAliveInstancesAsync(force: true);
                JsonArray instanceList = new();
                foreach ((int port, JsonNode identity) in alive)
                    instanceList.Add(new JsonObject { ["port"] = port, ["identity"] = CloneNode(identity) });
                await RespondAsync(ctx, 200, RpcResult(id, ToolResult(new JsonObject
                {
                    ["count"] = alive.Count,
                    ["instances"] = instanceList,
                }.ToJsonString(), false)).ToJsonString());
                return;
            }

        case "launch_game":
            {
                int count = (int?)args["count"]?.GetValue<double>() ?? 1;
                count = Math.Clamp(count, 1, 32);
                // "game_args" preferred; legacy "args" still accepted. Renamed because agents
                // wrapping tool calls in PowerShell kept mirroring the key as the automatic
                // variable $args, which silently shadows function parameters.
                JsonArray commandLineArgs = args["game_args"] as JsonArray ?? args["args"] as JsonArray ?? new JsonArray();
                if (!File.Exists(gameExe))
                {
                    await RespondAsync(ctx, 200, RpcResult(id, ToolResult($"Game exe not found: {gameExe} (set UCH_DIR env var).", true)).ToJsonString());
                    return;
                }
                string setupError = SanityCheckModSetup(out string setupWarning);
                if (setupError != null)
                {
                    await RespondAsync(ctx, 200, RpcResult(id, ToolResult($"Not launching: {setupError}", true)).ToJsonString());
                    return;
                }
                List<int> pids = new();
                for (int i = 0; i < count; i++)
                {
                    ProcessStartInfo startInfo = new()
                    {
                        FileName = gameExe,
                        WorkingDirectory = gameDir,
                        UseShellExecute = true,
                    };
                    foreach (JsonNode argument in commandLineArgs)
                    {
                        if (argument != null)
                            startInfo.ArgumentList.Add(argument.GetValue<string>());
                    }

                    Process p = Process.Start(startInfo);
                    pids.Add(p.Id);
                    if (count > 1)
                        await Task.Delay(1500); // stagger so port scan order is deterministic
                }
                string launchMsg = $"Launched {count} instance(s), pid(s): {string.Join(", ", pids)}. " +
                    "Bridges come up ~5-15s after launch; poll list_instances.";
                if (setupWarning != null)
                    launchMsg += $"\nWARNING: {setupWarning}";
                await RespondAsync(ctx, 200, RpcResult(id, ToolResult(launchMsg, false)).ToJsonString());
                return;
            }

        case "kill_game":
            {
                int? pid = (int?)args["pid"]?.GetValue<double>();
                List<string> killed = new();
                foreach (Process p in Process.GetProcessesByName("UltimateChickenHorse"))
                {
                    if (pid != null && p.Id != pid.Value)
                        continue;
                    try { p.Kill(); killed.Add(p.Id.ToString()); } catch { }
                }
                await RespondAsync(ctx, 200, RpcResult(id, ToolResult(
                    killed.Count > 0 ? $"Killed pid(s): {string.Join(", ", killed)}" : "No matching game process found.", false)).ToJsonString());
                return;
            }

        case "postmortem":
            {
                int lines = (int?)args["lines"]?.GetValue<double>() ?? 100;
                lines = Math.Clamp(lines, 10, 1000);
                JsonObject result = new();
                result["note"] = "Log tails from disk; readable even when the game is down or crashed.";
                AddLogTail(result, "unityexplorer", NewestFile(Path.Combine(gameDir, @"BepInEx\plugins\zmarn-dev-UltimateGlorpExplorer\Logs")), lines);
                AddLogTail(result, "bepinex", Path.Combine(gameDir, @"BepInEx\LogOutput.log"), lines);
                AddLogTail(result, "unity_player", Path.Combine(gameDir, "output_log.txt"), lines);
                await RespondAsync(ctx, 200, RpcResult(id, ToolResult(result.ToJsonString(), false)).ToJsonString());
                return;
            }

        case "save_skill":
            {
                string title = ReadStringArg(args, "title");
                string code = ReadStringArg(args, "code");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(code))
                {
                    await RespondAsync(ctx, 200, RpcResult(id, ToolResult("save_skill requires non-empty 'title' and 'code'.", true)).ToJsonString());
                    return;
                }
                try
                {
                    (string slug, bool updated) = SaveSkill(title, code, ReadStringArrayArg(args, "tags"), ReadStringArg(args, "comment"), "save_skill", null);
                    await RespondAsync(ctx, 200, RpcResult(id, ToolResult(
                        $"{(updated ? "Updated" : "Saved")} skill '{slug}' ({Path.Combine(skillsDir, slug + ".md")}).", false)).ToJsonString());
                }
                catch (Exception ex)
                {
                    await RespondAsync(ctx, 200, RpcResult(id, ToolResult($"Failed to save skill: {ex.Message}", true)).ToJsonString());
                }
                return;
            }

        case "list_skills":
            {
                List<(string Slug, string Title, List<string> Tags, string Summary)> skills = ListSkills();
                JsonArray skillArray = new();
                foreach ((string slug, string title, List<string> tags, string summary) in skills)
                {
                    JsonArray tagArray = new();
                    foreach (string tag in tags)
                        tagArray.Add(tag);
                    skillArray.Add(new JsonObject { ["slug"] = slug, ["title"] = title, ["tags"] = tagArray, ["summary"] = summary });
                }
                JsonObject listing = new() { ["count"] = skills.Count, ["skills"] = skillArray };
                if (skills.Count == 0)
                    listing["hint"] = "No skills saved yet. Save one via save_skill, or execute_csharp with a title.";
                await RespondAsync(ctx, 200, RpcResult(id, ToolResult(listing.ToJsonString(), false)).ToJsonString());
                return;
            }

        case "get_skill":
            {
                string name = ReadStringArg(args, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    await RespondAsync(ctx, 200, RpcResult(id, ToolResult("get_skill requires 'name' (slug or title, see list_skills).", true)).ToJsonString());
                    return;
                }
                // Slugify is idempotent on slugs, so this resolves both raw titles and slugs
                // (and keeps arbitrary client input from ever reaching the filesystem).
                string skillPath = Path.Combine(skillsDir, Slugify(name) + ".md");
                if (!File.Exists(skillPath))
                {
                    await RespondAsync(ctx, 200, RpcResult(id, ToolResult(
                        $"No skill '{name}'. Available: [{string.Join(", ", ListSkills().Select(s => s.Slug))}]", true)).ToJsonString());
                    return;
                }
                string skillText = File.ReadAllText(skillPath);
                if (ParseSkillFile(skillPath) == null)
                    skillText = "NOTE: this skill file did not parse cleanly (hand-edited?); raw content follows.\n\n" + skillText;
                await RespondAsync(ctx, 200, RpcResult(id, ToolResult(skillText, false)).ToJsonString());
                return;
            }
    }

    // Game tools: route by _port (stripped before forwarding), default = first alive instance.
    int? targetPort = (int?)(args["_port"]?.GetValue<double>());
    if (args.ContainsKey("_port"))
    {
        args.Remove("_port");
        req["params"]["arguments"] = args;
    }

    // execute_csharp skill capture: a title means "save this as a skill if it runs ok".
    // The metadata fields are proxy-injected, so strip them even when title is absent —
    // the game's REPL must never see them.
    string skillTitle = null, skillComment = null, skillCode = null;
    List<string> skillTags = null;
    if (toolName == "execute_csharp")
    {
        skillCode = ReadStringArg(args, "code");
        if (args.ContainsKey("title") || args.ContainsKey("tags") || args.ContainsKey("comment"))
        {
            skillTitle = ReadStringArg(args, "title");
            skillComment = ReadStringArg(args, "comment");
            skillTags = ReadStringArrayArg(args, "tags");
            args.Remove("title");
            args.Remove("tags");
            args.Remove("comment");
            req["params"]["arguments"] = args;
            if (string.IsNullOrWhiteSpace(skillTitle))
                Console.WriteLine("uch-mcp-proxy: execute_csharp had tags/comment but no title; not saving a skill");
        }
    }

    List<(int Port, JsonNode Identity)> instances = await GetAliveInstancesAsync();
    int port2;
    if (targetPort != null)
    {
        if (!instances.Any(it => it.Port == targetPort.Value))
        {
            await RespondAsync(ctx, 200, RpcResult(id, ToolResult(
                $"No game instance on port {targetPort}. Alive: [{string.Join(", ", instances.Select(it => it.Port))}] (see list_instances).", true)).ToJsonString());
            return;
        }
        port2 = targetPort.Value;
    }
    else if (instances.Count > 0)
    {
        port2 = instances[0].Port;
    }
    else
    {
        await RespondAsync(ctx, 200, RpcResult(id, ToolResult(
            "No game instance is running. Use launch_game to start one, then retry.", true)).ToJsonString());
        return;
    }

    if (toolName == "execute_csharp")
    {
        // Need to inspect the game's response before replying, so bypass TryForwardAsync
        // (which streams straight to the client): failed runs are appended to
        // csharp-failures.jsonl, and a title means "save as a skill on confirmed ok:true".
        string respBody = await ForwardForBodyAsync(port2, req.ToJsonString());
        if (respBody == null)
        {
            await RespondAsync(ctx, 200, RpcResult(id, ToolResult(
                $"Instance on port {port2} stopped responding mid-call.", true)).ToJsonString());
            return;
        }
        bool parsed = TryExtractExecOutcome(respBody, out bool execOk, out string resultSnippet);
        if (parsed && !execOk)
            LogCsharpFailure(port2, skillCode, resultSnippet);
        if (!string.IsNullOrWhiteSpace(skillTitle) && !string.IsNullOrWhiteSpace(skillCode))
        {
            if (parsed && execOk)
            {
                try
                {
                    (string slug, bool updated) = SaveSkill(skillTitle, skillCode, skillTags, skillComment, "execute_csharp", resultSnippet);
                    respBody = AppendTextBlock(respBody, $"{(updated ? "Updated" : "Saved")} skill '{slug}'.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"uch-mcp-proxy: skill save failed: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"uch-mcp-proxy: execute_csharp did not succeed; skill '{skillTitle}' not saved");
            }
        }
        await RespondAsync(ctx, 200, respBody);
        return;
    }

    if (!await TryForwardAsync(ctx, port2, req.ToJsonString()))
        await RespondAsync(ctx, 200, RpcResult(id, ToolResult(
            $"Instance on port {port2} stopped responding mid-call.", true)).ToJsonString());
}

async Task HandleToolsListAsync(HttpListenerContext ctx, JsonNode id, string body)
{
    JsonNode upstreamList = null;

    List<(int Port, JsonNode Identity)> alive = await GetAliveInstancesAsync();
    if (alive.Count > 0)
    {
        try
        {
            using HttpResponseMessage resp = await http.PostAsync($"http://127.0.0.1:{alive[0].Port}/mcp",
                new StringContent(body, Encoding.UTF8, "application/json"));
            string respBody = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode && respBody.Contains("\"tools\""))
            {
                upstreamList = JsonNode.Parse(respBody);
                try { File.WriteAllText(cachePath, respBody); } catch { }
            }
        }
        catch { }
    }

    if (upstreamList == null && File.Exists(cachePath))
    {
        try { upstreamList = JsonNode.Parse(File.ReadAllText(cachePath)); } catch { }
    }

    JsonArray tools = upstreamList?["result"]?["tools"] as JsonArray ?? new JsonArray();

    // Inject optional _port into every game tool so agents can target instances.
    foreach (JsonNode tool in tools)
    {
        JsonObject props = tool?["inputSchema"]?["properties"] as JsonObject;
        if (props != null && !props.ContainsKey("_port"))
            props["_port"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Target game instance port (from list_instances). Default: first alive instance.",
            };

        // Skill-library capture fields on execute_csharp (stripped by the proxy before forwarding).
        if (props != null && tool?["name"]?.GetValue<string>() == "execute_csharp" && !props.ContainsKey("title"))
        {
            props["title"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "If set, saves this code to the proxy skill library after a successful run. " +
                    "Prefer saving parameterizable, scene-independent, reusable snippets.",
            };
            props["tags"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Skill library tags (only used with title).",
            };
            props["comment"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Usage notes stored with the skill: what it does, parameters to tweak, assumptions (only used with title).",
            };
        }
    }

    // Proxy-local tools, available even with zero instances.
    tools.Add(ProxyTool("list_instances",
        "List running game instances (port, pid, steam name, utc clock). Use the port as _port on any game tool to target that instance.",
        new JsonObject()));
    tools.Add(ProxyTool("launch_game",
        "Launch 1-32 Ultimate Chicken Horse instances (for networked testing launch several; each gets its own bridge port). " +
        "Bridges come up ~5-15s later; poll list_instances.",
        new JsonObject
        {
            ["count"] = new JsonObject { ["type"] = "integer", ["description"] = "Instances to launch (default 1, max 32)." },
            ["game_args"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Command-line arguments passed to every launched game instance.",
            },
        }));
    tools.Add(ProxyTool("kill_game",
        "Kill game process(es). Without pid, kills ALL running instances. Use before redeploying the mod DLL (the file is locked while any instance runs).",
        new JsonObject { ["pid"] = new JsonObject { ["type"] = "integer", ["description"] = "Specific pid (from list_instances identity). Optional." } }));
    tools.Add(ProxyTool("postmortem",
        "Read log tails from disk (UnityExplorer, BepInEx, Unity player) — works even when the game is down, hung or crashed. " +
        "First stop after an unexpected death.",
        new JsonObject { ["lines"] = new JsonObject { ["type"] = "integer", ["description"] = "Tail length per log (default 100, max 1000)." } }));
    tools.Add(ProxyTool("save_skill",
        "Save a C# snippet to the proxy skill library without executing it (stored as markdown next to the proxy). " +
        "Prefer parameterizable, scene-independent, reusable code. Overwrites an existing skill with the same slug.",
        new JsonObject
        {
            ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Skill name; becomes the filename slug. Required." },
            ["code"] = new JsonObject { ["type"] = "string", ["description"] = "The C# code. Required." },
            ["tags"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Tags for discovery.",
            },
            ["comment"] = new JsonObject { ["type"] = "string", ["description"] = "Usage notes: what it does, parameters to tweak, assumptions." },
        }));
    tools.Add(ProxyTool("list_skills",
        "List saved skills (slug, title, tags, one-line summary). Skills are reusable execute_csharp snippets; fetch one with get_skill.",
        new JsonObject()));
    tools.Add(ProxyTool("get_skill",
        "Fetch a saved skill's full markdown (code + usage notes).",
        new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Skill slug or title from list_skills." } }));

    await RespondAsync(ctx, 200, RpcResult(id, new JsonObject { ["tools"] = CloneNode(tools) }).ToJsonString());
}

async Task<List<(int Port, JsonNode Identity)>> GetAliveInstancesAsync(bool force = false)
{
    if (!force && (DateTime.UtcNow - aliveCacheTime).TotalSeconds < 2)
        return aliveCache;

    List<Task<(int, JsonNode)>> probes = new();
    for (int port = UpstreamBasePort; port < UpstreamBasePort + UpstreamPortRange; port++)
    {
        if (port == ListenPort) continue;
        int p = port;
        probes.Add(Task.Run(async () =>
        {
            try
            {
                string resp = await probe.GetStringAsync($"http://127.0.0.1:{p}/");
                JsonNode json = JsonNode.Parse(resp);
                if (json?["name"]?.GetValue<string>()?.Contains("AI Bridge") == true)
                    return (p, json["instance"]);
            }
            catch { }
            return (0, (JsonNode)null);
        }));
    }

    (int, JsonNode)[] results = await Task.WhenAll(probes);
    aliveCache = results.Where(r => r.Item1 != 0).OrderBy(r => r.Item1).ToList();
    aliveCacheTime = DateTime.UtcNow;
    return aliveCache;
}

async Task<bool> TryForwardAsync(HttpListenerContext ctx, int port, string body)
{
    try
    {
        using HttpResponseMessage upstream = await http.PostAsync($"http://127.0.0.1:{port}/mcp",
            new StringContent(body, Encoding.UTF8, "application/json"));
        string responseBody = await upstream.Content.ReadAsStringAsync();
        await RespondAsync(ctx, (int)upstream.StatusCode, responseBody);
        return true;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return false;
    }
}

// Pre-launch sanity check: is the UnityExplorer bridge DLL actually going to load?
// Returns an error string (don't launch) or null; 'warning' carries non-fatal issues.
string SanityCheckModSetup(out string warning)
{
    warning = null;

    // 1. BepInEx doorstop present and enabled (BepInEx 5 on Windows = winhttp.dll + doorstop_config.ini).
    if (!File.Exists(Path.Combine(gameDir, "winhttp.dll")))
        return $"BepInEx doorstop (winhttp.dll) not found in {gameDir} — BepInEx is not installed, no plugin will load.";
    string doorstopIni = Path.Combine(gameDir, "doorstop_config.ini");
    if (File.Exists(doorstopIni))
    {
        foreach (string line in File.ReadAllLines(doorstopIni))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("enabled", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains('=')
                && trimmed.Split('=')[1].Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                return $"Doorstop is disabled (enabled=false in {doorstopIni}) — BepInEx will not load.";
        }
    }

    // 2. Our DLL present in BepInEx\plugins (root or any subfolder).
    string pluginsDir = Path.Combine(gameDir, @"BepInEx\plugins");
    if (!Directory.Exists(pluginsDir))
        return $"BepInEx plugins directory not found: {pluginsDir}";
    string deployedDll = Directory.GetFiles(pluginsDir, "UltimateGlorpExplorer*.dll", SearchOption.AllDirectories)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();
    if (deployedDll == null)
        return $"No UltimateGlorpExplorer*.dll found under {pluginsDir} — deploy the mod DLL first " +
            @"(build_uch.ps1, then copy Release\UnityExplorer.BepInEx5.Mono\UltimateGlorpExplorer.BIE5.Mono.dll there).";

    // 3. AI bridge not disabled in the UnityExplorer config (missing cfg = defaults = enabled).
    string cfg = Path.Combine(gameDir, @"BepInEx\config\com.sinai.unityexplorer.cfg");
    if (File.Exists(cfg))
    {
        foreach (string line in File.ReadAllLines(cfg))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("AI Bridge Port", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
            {
                string value = trimmed.Split('=')[1].Trim();
                if (int.TryParse(value, out int cfgPort) && cfgPort <= 0)
                    return $"AI Bridge is disabled ('AI Bridge Port = {cfgPort}' in {cfg}) — the game would start without the bridge.";
                break;
            }
        }
    }

    // 4. Non-fatal: deployed DLL older than the freshly built one in the repo's Release folder.
    string repoDll = FindRepoBuildDll();
    if (repoDll != null && File.GetLastWriteTimeUtc(repoDll) > File.GetLastWriteTimeUtc(deployedDll).AddSeconds(2))
        warning = $"Deployed DLL ({deployedDll}, {File.GetLastWriteTimeUtc(deployedDll):u}) is older than the last build " +
            $"({repoDll}, {File.GetLastWriteTimeUtc(repoDll):u}) — kill_game, copy the new DLL, launch again if you meant to test new code.";

    return null;
}

// Walks up from the proxy binary looking for the repo's built DLL (best effort).
static string FindRepoBuildDll()
{
    try
    {
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, @"Release\UnityExplorer.BepInEx5.Mono\UltimateGlorpExplorer.BIE5.Mono.dll");
            if (File.Exists(candidate))
                return candidate;
        }
    }
    catch { }
    return null;
}

static void AddLogTail(JsonObject result, string key, string path, int lines)
{
    try
    {
        if (path == null || !File.Exists(path))
        {
            result[key] = $"<not found: {path ?? "no log file"}>";
            return;
        }
        // Read with FileShare.ReadWrite: the game may hold the file open.
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(fs);
        List<string> all = new();
        string line;
        while ((line = reader.ReadLine()) != null)
            all.Add(line);
        result[key] = string.Join("\n", all.Skip(Math.Max(0, all.Count - lines)));
    }
    catch (Exception ex)
    {
        result[key] = $"<error reading {path}: {ex.Message}>";
    }
}

static string NewestFile(string dir)
{
    try
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }
    catch { return null; }
}

// ---- Skill library: markdown files in skills/ next to the proxy exe. ----------------
// Format: `---` frontmatter (title, tags, created, updated, source), a fenced csharp
// block, free-form comment text, optional trailing `<!-- last-result: ... -->`.
// Files are meant to be hand-editable; parsing degrades instead of throwing.

// The slug is the only string that ever touches the filesystem. Idempotent on its own output.
static string Slugify(string title)
{
    StringBuilder sb = new();
    bool lastDash = true;
    foreach (char c in (title ?? "").ToLowerInvariant())
    {
        if (char.IsLetterOrDigit(c)) { sb.Append(c); lastDash = false; }
        else if (!lastDash) { sb.Append('-'); lastDash = true; }
        if (sb.Length >= 64) break;
    }
    string slug = sb.ToString().Trim('-');
    if (slug.Length == 0)
        return "skill";
    string[] reserved = { "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" };
    return reserved.Contains(slug) ? "skill-" + slug : slug;
}

(string Slug, bool Updated) SaveSkill(string title, string code, List<string> tags, string comment, string source, string resultSnippet)
{
    Directory.CreateDirectory(skillsDir);
    string slug = Slugify(title);
    string path = Path.Combine(skillsDir, slug + ".md");
    bool updated = File.Exists(path);
    string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    string created = now;
    if (updated)
    {
        (string Title, List<string> Tags, string Created, string Code, string Comment)? existing = ParseSkillFile(path);
        if (!string.IsNullOrEmpty(existing?.Created))
            created = existing.Value.Created;
        if (existing != null && existing.Value.Title != title)
            Console.WriteLine($"uch-mcp-proxy: skill '{slug}' title changed ('{existing.Value.Title}' -> '{title}')");
    }

    static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
    string fence = (code ?? "").Contains("```") ? "````" : "```";
    StringBuilder md = new();
    md.AppendLine("---");
    md.AppendLine($"title: {OneLine(title)}");
    md.AppendLine($"tags: {string.Join(", ", (tags ?? new List<string>()).Select(OneLine))}");
    md.AppendLine($"created: {created}");
    md.AppendLine($"updated: {now}");
    md.AppendLine($"source: {source}");
    md.AppendLine("---");
    md.AppendLine();
    md.AppendLine(fence + "csharp");
    md.AppendLine((code ?? "").TrimEnd());
    md.AppendLine(fence);
    if (!string.IsNullOrWhiteSpace(comment))
    {
        md.AppendLine();
        md.AppendLine(comment.Trim());
    }
    if (!string.IsNullOrWhiteSpace(resultSnippet))
    {
        string snippet = resultSnippet.Replace("-->", "-- >");
        if (snippet.Length > 500)
            snippet = snippet.Substring(0, 500) + "...";
        md.AppendLine();
        md.AppendLine($"<!-- last-result: {snippet} -->");
    }
    lock (skillWriteLock)
        File.WriteAllText(path, md.ToString());
    Console.WriteLine($"uch-mcp-proxy: {(updated ? "updated" : "saved")} skill '{slug}' ({source})");
    return (slug, updated);
}

// Tolerant of hand-edited files: missing frontmatter -> title falls back to the slug,
// missing fence -> no code. Returns null only when the file is unreadable.
(string Title, List<string> Tags, string Created, string Code, string Comment)? ParseSkillFile(string path)
{
    try
    {
        string[] lines = File.ReadAllLines(path);
        string title = Path.GetFileNameWithoutExtension(path);
        List<string> tags = new();
        string created = null;
        int i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0)
            i++;
        if (i < lines.Length && lines[i].Trim() == "---")
        {
            i++;
            for (; i < lines.Length && lines[i].Trim() != "---"; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon < 0)
                    continue;
                string key = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                string value = lines[i].Substring(colon + 1).Trim();
                switch (key)
                {
                    case "title": if (value.Length > 0) title = value; break;
                    case "tags": tags = value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList(); break;
                    case "created": created = value; break;
                }
            }
            if (i < lines.Length)
                i++; // skip closing ---
        }

        string code = null;
        int bodyStart = i;
        int fenceStart = -1;
        for (int j = i; j < lines.Length; j++)
            if (lines[j].TrimStart().StartsWith("```")) { fenceStart = j; break; }
        if (fenceStart >= 0)
        {
            int fenceLen = lines[fenceStart].Trim().TakeWhile(c => c == '`').Count();
            List<string> codeLines = new();
            int j = fenceStart + 1;
            for (; j < lines.Length; j++)
            {
                string t = lines[j].Trim();
                if (t.Length >= fenceLen && t.All(c => c == '`'))
                    break;
                codeLines.Add(lines[j]);
            }
            code = string.Join("\n", codeLines);
            bodyStart = j + 1;
        }

        string comment = string.Join("\n", lines.Skip(bodyStart));
        int marker = comment.IndexOf("<!-- last-result:", StringComparison.Ordinal);
        if (marker >= 0)
            comment = comment.Substring(0, marker);
        return (title, tags, created, code, comment.Trim());
    }
    catch { return null; }
}

// A future search_skills is just this plus full-text filtering over ParseSkillFile output.
List<(string Slug, string Title, List<string> Tags, string Summary)> ListSkills()
{
    List<(string, string, List<string>, string)> skills = new();
    try
    {
        if (!Directory.Exists(skillsDir))
            return skills;
        foreach (string path in Directory.GetFiles(skillsDir, "*.md").OrderBy(p => p))
        {
            (string Title, List<string> Tags, string Created, string Code, string Comment)? parsed = ParseSkillFile(path);
            if (parsed == null)
            {
                Console.WriteLine($"uch-mcp-proxy: unreadable skill file skipped: {path}");
                continue;
            }
            string summary = parsed.Value.Comment?.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            skills.Add((Path.GetFileNameWithoutExtension(path), parsed.Value.Title, parsed.Value.Tags, summary));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"uch-mcp-proxy: list skills failed: {ex.Message}");
    }
    return skills;
}

// Every failed execute_csharp run gets a JSONL record — first-party data for
// "what C# do agents commonly get wrong" analysis (the alternative is mining transcripts).
void LogCsharpFailure(int port, string code, string error)
{
    try
    {
        string line = new JsonObject
        {
            ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["port"] = port,
            ["code"] = code,
            ["error"] = error,
        }.ToJsonString();
        lock (skillWriteLock)
            File.AppendAllText(failuresLogPath, line + Environment.NewLine);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"uch-mcp-proxy: failure-log write failed: {ex.Message}");
    }
}

// Like TryForwardAsync but returns the body instead of streaming it to the client,
// so the caller can inspect the outcome first. Null = instance gone.
async Task<string> ForwardForBodyAsync(int port, string body)
{
    try
    {
        using HttpResponseMessage upstream = await http.PostAsync($"http://127.0.0.1:{port}/mcp",
            new StringContent(body, Encoding.UTF8, "application/json"));
        return await upstream.Content.ReadAsStringAsync();
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return null;
    }
}

// Digs the { ok, result, error } payload out of an execute_csharp JSON-RPC response.
// Returns false when success can't be confirmed (error envelope, isError, unparseable text).
static bool TryExtractExecOutcome(string respBody, out bool ok, out string snippet)
{
    ok = false;
    snippet = null;
    try
    {
        JsonNode resp = JsonNode.Parse(respBody);
        if (resp?["error"] != null)
            return false;
        // Don't bail on result.isError: the bridge sets it whenever ok:false, and the
        // inner payload still carries the error text we want for the failure log.
        JsonNode result = resp?["result"];
        string text = (result?["content"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(c => c["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>();
        if (text == null)
            return false;
        JsonNode inner = JsonNode.Parse(text);
        ok = inner?["ok"]?.GetValue<bool>() == true;
        JsonNode payload = inner?["result"] ?? inner?["error"];
        snippet = payload is JsonValue v && v.TryGetValue(out string s) ? s : payload?.ToJsonString();
        return true;
    }
    catch { return false; }
}

static string AppendTextBlock(string respBody, string text)
{
    try
    {
        JsonNode resp = JsonNode.Parse(respBody);
        if (resp?["result"]?["content"] is not JsonArray content)
            return respBody;
        content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        return resp.ToJsonString();
    }
    catch { return respBody; }
}

// Defensive arg readers: clients occasionally send the wrong JSON type; treat as absent.
static string ReadStringArg(JsonObject args, string key)
{
    try { return args[key]?.GetValue<string>(); } catch { return null; }
}

static List<string> ReadStringArrayArg(JsonObject args, string key)
{
    List<string> values = new();
    if (args[key] is JsonArray arr)
        foreach (JsonNode node in arr)
        {
            try
            {
                string s = node?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(s))
                    values.Add(s);
            }
            catch { }
        }
    return values;
}

static JsonObject ProxyTool(string name, string description, JsonObject properties)
    => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties },
    };

static JsonObject ToolResult(string text, bool isError)
    => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = isError,
    };

static JsonObject RpcResult(JsonNode id, JsonObject result)
    => new() { ["jsonrpc"] = "2.0", ["id"] = CloneNode(id), ["result"] = result };

static JsonObject RpcError(JsonNode id, int code, string message)
    => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = CloneNode(id),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

static JsonNode CloneNode(JsonNode node)
    => node == null ? null : JsonNode.Parse(node.ToJsonString());

async Task RespondAsync(HttpListenerContext ctx, int status, string body)
{
    byte[] bytes = Encoding.UTF8.GetBytes(body);
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
    ctx.Response.Close();
}
