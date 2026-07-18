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
// Clients register http://127.0.0.1:7312/mcp. Game instances bind the first free
// port in [7311+? no: BASE_UPSTREAM..+16); the proxy scans that range and routes
// each tools/call to a specific instance via an injected optional "_port" argument.
// The proxy also answers four tools itself (they work with zero instances running):
//   list_instances, launch_game, kill_game, postmortem

const int ListenPort = 7312;
const int UpstreamBasePort = 7311;
const int UpstreamPortRange = 16;

string gameDir = Environment.GetEnvironmentVariable("UCH_DIR")
    ?? @"S:\SteamLibrary\steamapps\common\Ultimate Chicken Horse";
string gameExe = Path.Combine(gameDir, "UltimateChickenHorse.exe");
string cachePath = Path.Combine(AppContext.BaseDirectory, "tools-cache.json");

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
                            "list_instances to see what's up, and pass _port on any game tool to target a specific instance.",
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
                count = Math.Clamp(count, 1, 8);
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
                    Process p = Process.Start(new ProcessStartInfo { FileName = gameExe, WorkingDirectory = gameDir, UseShellExecute = true });
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
                AddLogTail(result, "unityexplorer", NewestFile(Path.Combine(gameDir, @"BepInEx\plugins\sinai-dev-UnityExplorer\Logs")), lines);
                AddLogTail(result, "bepinex", Path.Combine(gameDir, @"BepInEx\LogOutput.log"), lines);
                AddLogTail(result, "unity_player", Path.Combine(gameDir, "output_log.txt"), lines);
                await RespondAsync(ctx, 200, RpcResult(id, ToolResult(result.ToJsonString(), false)).ToJsonString());
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
    }

    // Proxy-local tools, available even with zero instances.
    tools.Add(ProxyTool("list_instances",
        "List running game instances (port, pid, steam name, utc clock). Use the port as _port on any game tool to target that instance.",
        new JsonObject()));
    tools.Add(ProxyTool("launch_game",
        "Launch 1-8 Ultimate Chicken Horse instances (for networked testing launch several; each gets its own bridge port). " +
        "Bridges come up ~5-15s later; poll list_instances.",
        new JsonObject { ["count"] = new JsonObject { ["type"] = "integer", ["description"] = "Instances to launch (default 1, max 8)." } }));
    tools.Add(ProxyTool("kill_game",
        "Kill game process(es). Without pid, kills ALL running instances. Use before redeploying the mod DLL (the file is locked while any instance runs).",
        new JsonObject { ["pid"] = new JsonObject { ["type"] = "integer", ["description"] = "Specific pid (from list_instances identity). Optional." } }));
    tools.Add(ProxyTool("postmortem",
        "Read log tails from disk (UnityExplorer, BepInEx, Unity player) — works even when the game is down, hung or crashed. " +
        "First stop after an unexpected death.",
        new JsonObject { ["lines"] = new JsonObject { ["type"] = "integer", ["description"] = "Tail length per log (default 100, max 1000)." } }));

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
    string deployedDll = Directory.GetFiles(pluginsDir, "UnityExplorer*.dll", SearchOption.AllDirectories)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();
    if (deployedDll == null)
        return $"No UnityExplorer*.dll found under {pluginsDir} — deploy the mod DLL first " +
            @"(build_uch.ps1, then copy Release\UnityExplorer.BepInEx5.Mono\UnityExplorer.BIE5.Mono.dll there).";

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
            string candidate = Path.Combine(dir.FullName, @"Release\UnityExplorer.BepInEx5.Mono\UnityExplorer.BIE5.Mono.dll");
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
