using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

// Tiny always-on MCP proxy for the UCH AI bridge.
// Clients register http://127.0.0.1:7312/mcp; requests are forwarded to the
// in-game server at 7311. While the game is down, the proxy answers the MCP
// lifecycle itself (initialize / tools/list from cache / clean tool errors)
// so agent sessions survive game restarts and can start before the game does.

const int ListenPort = 7312;
const string UpstreamUrl = "http://127.0.0.1:7311/mcp";
string cachePath = Path.Combine(AppContext.BaseDirectory, "tools-cache.json");

HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

HttpListener listener = new();
listener.Prefixes.Add($"http://127.0.0.1:{ListenPort}/");
listener.Start();
Console.WriteLine($"uch-mcp-proxy: listening on http://127.0.0.1:{ListenPort}/mcp -> {UpstreamUrl}");
Console.WriteLine($"uch-mcp-proxy: tools cache: {cachePath} ({(File.Exists(cachePath) ? "present" : "not yet cached — will fill on first contact with the game")})");

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
        try
        {
            JsonNode req = JsonNode.Parse(body);
            method = req?["method"]?.GetValue<string>();
            id = req?["id"];
        }
        catch { /* forward unparseable bodies as-is; upstream will complain */ }

        // Try the game first.
        try
        {
            using HttpResponseMessage upstream = await http.PostAsync(UpstreamUrl,
                new StringContent(body, Encoding.UTF8, "application/json"));
            string responseBody = await upstream.Content.ReadAsStringAsync();

            if (method == "tools/list" && upstream.IsSuccessStatusCode && responseBody.Contains("\"tools\""))
                TryWriteCache(responseBody);

            await RespondAsync(ctx, (int)upstream.StatusCode, responseBody);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Game not running (or hung) — answer ourselves.
        }

        switch (method)
        {
            case "initialize":
                string protocol = "2025-06-18";
                try { protocol = JsonNode.Parse(body)?["params"]?["protocolVersion"]?.GetValue<string>() ?? protocol; } catch { }
                await RespondAsync(ctx, 200, new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = CloneNode(id),
                    ["result"] = new JsonObject
                    {
                        ["protocolVersion"] = protocol,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "uch-ai-bridge-proxy", ["version"] = "1.0" },
                        ["instructions"] = "Tools for inspecting and scripting a live Ultimate Chicken Horse game process. " +
                            "The game is NOT currently running — tool calls will fail until the user launches it. " +
                            "Ask the user to start UCH, then simply retry.",
                    },
                }.ToJsonString());
                return;

            case "ping":
                await RespondAsync(ctx, 200, new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = CloneNode(id),
                    ["result"] = new JsonObject(),
                }.ToJsonString());
                return;

            case "tools/list":
                if (File.Exists(cachePath))
                {
                    try
                    {
                        JsonNode cached = JsonNode.Parse(File.ReadAllText(cachePath));
                        cached["id"] = CloneNode(id);
                        await RespondAsync(ctx, 200, cached.ToJsonString());
                        return;
                    }
                    catch { /* fall through to empty list */ }
                }
                await RespondAsync(ctx, 200, new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = CloneNode(id),
                    ["result"] = new JsonObject { ["tools"] = new JsonArray() },
                }.ToJsonString());
                return;

            case "tools/call":
                await RespondAsync(ctx, 200, new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = CloneNode(id),
                    ["result"] = new JsonObject
                    {
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = "The game is not running (connection to the in-game AI bridge on port 7311 failed). " +
                                    "Ask the user to launch Ultimate Chicken Horse, then retry this call.",
                            },
                        },
                        ["isError"] = true,
                    },
                }.ToJsonString());
                return;

            default:
                if (method != null && method.StartsWith("notifications/"))
                {
                    ctx.Response.StatusCode = 202;
                    ctx.Response.Close();
                    return;
                }
                await RespondAsync(ctx, 200, new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = CloneNode(id),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Game offline; method not handled by proxy: {method}" },
                }.ToJsonString());
                return;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"uch-mcp-proxy: error handling request: {ex.Message}");
        try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
    }
}

static JsonNode CloneNode(JsonNode node)
    => node == null ? null : JsonNode.Parse(node.ToJsonString());

void TryWriteCache(string responseBody)
{
    try { File.WriteAllText(cachePath, responseBody); }
    catch (Exception ex) { Console.WriteLine($"uch-mcp-proxy: could not write tools cache: {ex.Message}"); }
}

async Task RespondAsync(HttpListenerContext ctx, int status, string body)
{
    byte[] bytes = Encoding.UTF8.GetBytes(body);
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
    ctx.Response.Close();
}
