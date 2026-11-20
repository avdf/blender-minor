using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlenderMinor;

// BMLS: Blender Minor LLM Shortcuts. MCP (HTTP, stateless) with one tool per shape.
// The tools build the bpy script from fixed templates and send it to blender-mcp (execute_blender_code).

// Command-line modes (they do not start the server):
//   --selftest                  runs every tool in an isolated headless Blender and reports what broke
//   --dump-templates [folder]   writes the embedded texts as a starting point for overriding (deletes nothing)
if (args.Contains("--selftest")) return await SelfTest.RunAsync();
int dumpAt = Array.IndexOf(args, "--dump-templates");
if (dumpAt >= 0)
{
    string dumpDir = dumpAt + 1 < args.Length ? args[dumpAt + 1] : Templates.Dir;
    foreach (var line in Templates.Dump(dumpDir)) Console.WriteLine(line);
    return 0;
}

var builder = WebApplication.CreateBuilder(args);
string port = Environment.GetEnvironmentVariable("BLENDER_MINOR_PORT") ?? "9192";
// Default 127.0.0.1 (the API has NO authentication). BLENDER_MINOR_HOST=0.0.0.0 opens it to the local network: only as an explicit decision.
string host = Environment.GetEnvironmentVariable("BLENDER_MINOR_HOST") ?? "127.0.0.1";
builder.WebHost.UseUrls($"http://{host}:{port}");
var app = builder.Build();

string blenderUrl = Environment.GetEnvironmentVariable("BLENDER_MCP_URL") ?? "http://127.0.0.1:9191/";
bool dryRun = Environment.GetEnvironmentVariable("BLENDER_MINOR_DRYRUN") == "1"; // returns the script instead of sending it to Blender
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

app.MapGet("/health", () => dryRun ? "ok (dry-run)" : "ok");

// Shows where each tool's template comes from (override file or embedded text).
app.MapGet("/templates", () => Results.Json(new
{
    dir = Templates.Dir,
    tools = ShapeScripts.Shapes.ToDictionary(s => s.Tool, s => ShapeScripts.Sources(s)),
}));

app.MapPost("/", async (HttpContext ctx) =>
{
    JsonDocument doc;
    try { doc = await JsonDocument.ParseAsync(ctx.Request.Body); }
    catch (JsonException) { return Rpc.Error(null, -32700, "Parse error"); }
    using (doc)
    {
        var root = doc.RootElement;
        string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        JsonNode? id = root.TryGetProperty("id", out var idEl) ? JsonNode.Parse(idEl.GetRawText()) : null;
        if (id is null) return Results.StatusCode(202); // notification (e.g. notifications/initialized)

        switch (method)
        {
            case "initialize":
                return Rpc.Result(id, new JsonObject
                {
                    ["protocolVersion"] = root.TryGetProperty("params", out var pp) && pp.TryGetProperty("protocolVersion", out var pv) ? pv.GetString() : "2025-03-26",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "blender-minor", ["version"] = "0.1.0" },
                });
            case "ping":
                return Rpc.Result(id, new JsonObject());
            case "tools/list":
                return Rpc.Result(id, new JsonObject { ["tools"] = new JsonArray(ShapeScripts.Shapes.Select(Rpc.ToolDef).Append(Rpc.ListObjectsDef()).Concat(OpScripts.Ops.Select(OpScripts.ToolDef)).ToArray<JsonNode?>()) });
            case "tools/call":
            {
                var p = root.GetProperty("params");
                string tool = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                JsonElement args = p.TryGetProperty("arguments", out var a) ? a : default;
                if (tool == "list_objects")
                {
                    // Lists the scene as short text (name, type, position): a small model copies this better than the large JSON from blender-mcp.
                    if (dryRun) return Rpc.Result(id, Rpc.Text(Rpc.ListScript, false));
                    var (lt, lerr) = await Rpc.RunInBlenderAsync(http, blenderUrl, Rpc.ListScript, ctx.RequestAborted);
                    return Rpc.Result(id, Rpc.Text(lerr ? lt : Rpc.StdoutOrText(lt), lerr));
                }
                var op = OpScripts.Find(tool);
                if (op is not null)
                {
                    // Editing an existing object by name (move, bevel...): fixed script, validated values only.
                    var (oscript, oerr) = OpScripts.Build(op, args);
                    if (oerr is not null) return Rpc.Result(id, Rpc.Text(oerr, true));
                    if (dryRun) return Rpc.Result(id, Rpc.Text(oscript!, false));
                    var (otext, oIsErr) = await Rpc.RunInBlenderAsync(http, blenderUrl, oscript!, ctx.RequestAborted);
                    return Rpc.Result(id, Rpc.Text(oIsErr ? otext : Rpc.StdoutOrText(otext), oIsErr));
                }
                var shape = ShapeScripts.Find(tool);
                if (shape is null) return Rpc.Result(id, Rpc.Text($"Unknown tool '{tool}'. Tools: {string.Join(", ", ShapeScripts.Shapes.Select(s => s.Tool).Append("list_objects").Concat(OpScripts.Ops.Select(o => o.Tool)))}.", true));
                var (script, error) = ShapeScripts.Build(shape, args);
                if (error is not null) return Rpc.Result(id, Rpc.Text(error, true));
                if (dryRun) return Rpc.Result(id, Rpc.Text(script!, false));
                var (text, isErr) = await Rpc.RunInBlenderAsync(http, blenderUrl, script!, ctx.RequestAborted);
                return Rpc.Result(id, Rpc.Text(text, isErr));
            }
            default:
                return Rpc.Error(id, -32601, $"Method not found: {method}");
        }
    }
});

app.Run();
return 0;

static class Rpc
{
    public static IResult Result(JsonNode id, JsonNode result) =>
        Results.Content(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString(), "application/json");

    public static IResult Error(JsonNode? id, int code, string message) =>
        Results.Content(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(), "application/json");

    public static JsonObject Text(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    /// <summary>Fixed script (no model-supplied arguments): one line per scene object, "name TYPE (x, y, z)".</summary>
    public const string ListScript = "import bpy\nprint(len(bpy.context.scene.objects), 'objects')\nprint('\\n'.join(o.name + ' ' + o.type + ' ' + str(tuple(round(v, 2) for v in o.location)) for o in bpy.context.scene.objects))";

    public static JsonNode ListObjectsDef() => new JsonObject
    {
        ["name"] = "list_objects",
        ["description"] = "List the objects in the Blender scene (name, type, position). No arguments.",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    };

    /// <summary>blender-mcp returns JSON with "stdout"; the printed text is all the model needs.</summary>
    public static string StdoutOrText(string text)
    {
        try
        {
            using var d = JsonDocument.Parse(text);
            if (d.RootElement.TryGetProperty("stdout", out var s) && s.ValueKind == JsonValueKind.String) return s.GetString()!.Trim();
        }
        catch (JsonException) { }
        return text;
    }

    /// <summary>Short description on purpose: every token counts for a small model on a slow CPU.</summary>
    public static JsonNode ToolDef(Shape s)
    {
        string D(double v) => v.ToString(CultureInfo.InvariantCulture);
        var props = new JsonObject();
        foreach (var p in s.Params) props[p.Name] = new JsonObject { ["type"] = "number", ["description"] = $"default {D(p.Default)}" };
        props["x"] = new JsonObject { ["type"] = "number", ["description"] = "default 0" };
        props["y"] = new JsonObject { ["type"] = "number", ["description"] = "default 0" };
        props["z"] = new JsonObject { ["type"] = "number", ["description"] = "default 0" };
        props["color"] = new JsonObject { ["type"] = "string", ["description"] = "name (red, blue...) or #RRGGBB" };
        props["specular"] = new JsonObject { ["type"] = "number", ["description"] = "0 to 1" };
        props["roughness"] = new JsonObject { ["type"] = "number", ["description"] = "0 to 1" };
        props["name"] = new JsonObject { ["type"] = "string", ["description"] = $"default {s.Label}" };
        return new JsonObject
        {
            ["name"] = s.Tool,
            ["description"] = s.Description + " All arguments are optional.",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props },
        };
    }

    /// <summary>Calls execute_blender_code on blender-mcp (JSON or SSE) and returns the text plus whether it was an error.</summary>
    public static async Task<(string Text, bool IsError)> RunInBlenderAsync(HttpClient http, string url, string script, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "execute_blender_code", ["arguments"] = new JsonObject { ["code"] = script } },
        }.ToJsonString();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.Accept.ParseAdd("text/event-stream");
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            string raw = await resp.Content.ReadAsStringAsync(ct);
            if (resp.Content.Headers.ContentType?.MediaType == "text/event-stream")
                raw = raw.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()).LastOrDefault() ?? "{}";
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var err)) return ("Blender error: " + (err.TryGetProperty("message", out var m) ? m.GetString() : err.GetRawText()), true);
            var result = r.GetProperty("result");
            var sb = new StringBuilder();
            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var c in content.EnumerateArray())
                    if (c.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String) sb.Append(tx.GetString()).Append('\n');
            bool isErr = result.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
            string text = sb.ToString().TrimEnd();
            return (isErr ? "Blender error: " + text : text, isErr);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ($"Could not reach Blender ({e.Message}). Is blender-mcp running on {url}?", true);
        }
    }
}
