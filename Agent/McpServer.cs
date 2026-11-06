using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

/// <summary>
/// Model Context Protocol server exposing <see cref="MapTools"/>: JSON-RPC 2.0 over stdio (<c>--mcp</c>) or
/// Streamable HTTP on localhost (inside the GUI). <see cref="RunBridge"/> relays stdio to the GUI's HTTP endpoint.
/// </summary>
public sealed class McpServer : IDisposable
{
    public const int DefaultPort = 32271;
    public const string ServerName = AppInfo.McpName;
    private static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

    private readonly MapTools _tools;
    private HttpListener? _http;
    public int Port { get; private set; }
    public bool Running => _http?.IsListening == true;
    public string Url => $"http://localhost:{Port}/mcp";
    public event Action<string>? Log;

    public McpServer(MapTools tools) { _tools = tools; }

    // ---- protocol ----

    /// <summary>Handles one JSON-RPC message (or batch); returns the response, or null for notifications.</summary>
    public JsonNode? Handle(JsonNode? msg)
    {
        if (msg is JsonArray batch)
        {
            var results = new JsonArray();
            foreach (var m in batch) if (Handle(m) is { } r) results.Add(r);
            return results.Count == 0 ? null : results;
        }
        if (msg is not JsonObject o) return Error(null, -32600, "Invalid request");
        JsonNode? id = o["id"]?.DeepClone();
        string? method = o["method"]?.GetValue<string>();
        if (method == null) return null; // a response to something we never send
        bool notification = !o.ContainsKey("id");
        try
        {
            JsonNode? result = Dispatch(method, o["params"] as JsonObject);
            return notification ? null : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (RpcException ex) { return notification ? null : Error(id, ex.Code, ex.Message); }
        catch (Exception ex) { return notification ? null : Error(id, -32603, ex.Message); }
    }

    private JsonNode? Dispatch(string method, JsonObject? p)
    {
        switch (method)
        {
            case "initialize":
                string wanted = p?["protocolVersion"]?.GetValue<string>() ?? Versions[0];
                return new JsonObject
                {
                    ["protocolVersion"] = Versions.Contains(wanted) ? wanted : Versions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false }, ["prompts"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = AppInfo.Title, ["version"] = AppInfo.Version },
                    ["instructions"] = MapTools.Instructions
                };
            case "ping":
                return new JsonObject();
            case "tools/list":
                var list = new JsonArray();
                foreach (var t in _tools.All)
                    list.Add(new JsonObject
                    {
                        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.InputSchema.DeepClone(),
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = t.ReadOnly, ["destructiveHint"] = !t.ReadOnly && t.Name is "delete_room" or "remove_entity" or "save_map" }
                    });
                return new JsonObject { ["tools"] = list };
            case "tools/call":
                string name = p?["name"]?.GetValue<string>() ?? throw new RpcException(-32602, "Missing tool name");
                if (!_tools.All.Any(t => t.Name == name)) throw new RpcException(-32602, "Unknown tool: " + name);
                Log?.Invoke($"MCP tool: {name}");
                return _tools.Call(name, p["arguments"] as JsonObject).ToJson();
            case "resources/list": return new JsonObject { ["resources"] = new JsonArray() };
            case "prompts/list": return new JsonObject { ["prompts"] = Prompts.List() };
            case "prompts/get":
                string pname = p?["name"]?.GetValue<string>() ?? throw new RpcException(-32602, "Missing prompt name");
                try { return Prompts.Get(pname, p["arguments"] as JsonObject); }
                catch (ToolException ex) { throw new RpcException(-32602, ex.Message); }
            default:
                if (method.StartsWith("notifications/")) return null;
                throw new RpcException(-32601, "Method not found: " + method);
        }
    }

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private sealed class RpcException(int code, string message) : Exception(message) { public int Code = code; }

    private static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    // ---- stdio ----

    /// <summary>Newline-delimited JSON-RPC on stdin/stdout until stdin closes.</summary>
    public void RunStdio()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        while (input.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            var msg = Parse(line);
            JsonNode? resp = msg == null ? Error(null, -32700, "Parse error") : Handle(msg);
            if (resp != null) output.WriteLine(resp.ToJsonString(MapTools.JsonOut));
        }
    }

    // ---- Streamable HTTP ----

    public void StartHttp(int port)
    {
        Stop();
        var l = new HttpListener();
        l.Prefixes.Add($"http://localhost:{port}/");
        l.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { l.Start(); }
        catch (HttpListenerException)
        {
            // Without a URL ACL only "localhost" may be bound by a non-admin user.
            l.Close();
            l = new HttpListener();
            l.Prefixes.Add($"http://localhost:{port}/");
            l.Start();
        }
        _http = l;
        Port = port;
        new Thread(() => Listen(l)) { IsBackground = true, Name = "MCP HTTP" }.Start();
    }

    public void Stop()
    {
        try { _http?.Stop(); _http?.Close(); } catch { }
        _http = null;
    }

    public void Dispose() => Stop();

    private void Listen(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = l.GetContext(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Serve(ctx));
        }
    }

    private void Serve(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            // DNS-rebinding guard: only local pages may talk to us.
            string? origin = req.Headers["Origin"];
            if (origin != null && !(Uri.TryCreate(origin, UriKind.Absolute, out var ou) && (ou.IsLoopback || ou.Host == "localhost")))
            {
                Reply(resp, 403, null);
                return;
            }
            if (req.Url?.AbsolutePath.TrimEnd('/') != "/mcp") { Reply(resp, 404, null); return; }
            if (req.HttpMethod == "DELETE") { Reply(resp, 200, null); return; }
            if (req.HttpMethod != "POST") { resp.AddHeader("Allow", "POST"); Reply(resp, 405, null); return; }

            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
            var msg = Parse(body);
            if (msg == null) { Reply(resp, 400, Error(null, -32700, "Parse error").ToJsonString(MapTools.JsonOut)); return; }
            if (msg is JsonObject { } o && o["method"]?.ToString() == "initialize")
                resp.AddHeader("Mcp-Session-Id", Guid.NewGuid().ToString("N"));
            var result = Handle(msg);
            if (result == null) Reply(resp, 202, null);
            else Reply(resp, 200, result.ToJsonString(MapTools.JsonOut));
        }
        catch (Exception ex)
        {
            try { Reply(resp, 500, Error(null, -32603, ex.Message).ToJsonString(MapTools.JsonOut)); } catch { }
        }
    }

    private static void Reply(HttpListenerResponse resp, int status, string? json)
    {
        resp.StatusCode = status;
        if (json != null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentType = "application/json";
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes);
        }
        resp.Close();
    }

    // ---- stdio → HTTP bridge ----

    /// <summary>Relays newline-delimited JSON-RPC from stdin to a running viewer's HTTP endpoint (for stdio-only clients).</summary>
    public static int RunBridge(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        string? session = null;
        while (input.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            JsonNode? id = null;
            try { id = (JsonNode.Parse(line) as JsonObject)?["id"]?.DeepClone(); } catch { }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(line, Encoding.UTF8, "application/json") };
                req.Headers.Accept.ParseAdd("application/json, text/event-stream");
                if (session != null) req.Headers.Add("Mcp-Session-Id", session);
                using var resp = http.Send(req);
                if (resp.Headers.TryGetValues("Mcp-Session-Id", out var s)) session = s.FirstOrDefault();
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
                if (body.Length > 0) output.WriteLine(JsonNode.Parse(body)!.ToJsonString(MapTools.JsonOut));
            }
            catch (Exception ex)
            {
                if (id != null)
                    output.WriteLine(Error(id, -32603,
                        $"{AppInfo.Title} is not reachable at {url} ({ex.Message}). Open MT_Zenith and enable AI ▸ MCP server.").ToJsonString(MapTools.JsonOut));
            }
        }
        return 0;
    }
}
