using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

/// <summary>
/// Agent Client Protocol client: runs an agent (Claude Code, Codex, Copilot CLI, Gemini CLI, ...) as a subprocess and
/// speaks newline-delimited JSON-RPC 2.0 over its stdio. See https://agentclientprotocol.com.
/// </summary>
public sealed class AcpClient : IDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private long _nextId;

    /// <summary>Agent → client requests (method, params) → result. Throw <see cref="AcpError"/> to return an error.</summary>
    public Func<string, JsonObject?, Task<JsonNode?>>? RequestHandler;
    public event Action<string, JsonObject?>? Notification;
    public event Action<string>? StdErr;
    public event Action? Exited;

    public JsonObject? AgentInfo { get; private set; }
    public JsonObject? AgentCapabilities { get; private set; }
    public JsonArray? AuthMethods { get; private set; }

    private AcpClient(Process proc)
    {
        _proc = proc;
        _stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        new Thread(ReadLoop) { IsBackground = true, Name = "ACP stdout" }.Start();
        new Thread(() =>
        {
            while (proc.StandardError.ReadLine() is { } l) StdErr?.Invoke(l);
        }) { IsBackground = true, Name = "ACP stderr" }.Start();
    }

    /// <summary>Starts the agent command line (run through cmd.exe so npx/.cmd shims resolve).</summary>
    public static AcpClient Start(string commandLine, string cwd)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/s");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(commandLine);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start: " + commandLine);
        p.EnableRaisingEvents = true;
        var client = new AcpClient(p);
        p.Exited += (_, _) => client.OnExit();
        return client;
    }

    public bool HasExited
    {
        get { try { return _proc.HasExited; } catch { return true; } }
    }

    private void OnExit()
    {
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var tcs)) tcs.TrySetException(new AcpError(-32603, "The agent process exited."));
        Exited?.Invoke();
    }

    // ---- protocol helpers ----

    public async Task<JsonObject> InitializeAsync()
    {
        var res = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = true, ["writeTextFile"] = true },
                ["terminal"] = false,
                ["session"] = new JsonObject { ["configOptions"] = new JsonObject { ["boolean"] = new JsonObject() } }
            },
            ["clientInfo"] = new JsonObject { ["name"] = AppInfo.McpName, ["title"] = AppInfo.Title, ["version"] = AppInfo.Version }
        }) as JsonObject ?? new JsonObject();
        AgentInfo = res["agentInfo"] as JsonObject;
        AgentCapabilities = res["agentCapabilities"] as JsonObject;
        AuthMethods = res["authMethods"] as JsonArray;
        return res;
    }

    public bool SupportsHttpMcp => AgentCapabilities?["mcpCapabilities"]?["http"]?.GetValue<bool>() == true;

    /// <summary>Creates a session; the result also carries <c>configOptions</c> and the legacy <c>models</c>/<c>modes</c>.</summary>
    public async Task<JsonObject> NewSessionAsync(string cwd, JsonArray mcpServers)
    {
        var res = await RequestAsync("session/new", new JsonObject { ["cwd"] = cwd, ["mcpServers"] = mcpServers }) as JsonObject;
        if (res?["sessionId"]?.GetValue<string>() is not { Length: > 0 })
            throw new AcpError(-32603, "session/new returned no sessionId");
        return res;
    }

    /// <summary>Sets a session config option; returns the agent's complete new <c>configOptions</c> list (may be null).</summary>
    public async Task<JsonArray?> SetConfigOptionAsync(string sessionId, string configId, JsonNode value)
    {
        var p = new JsonObject { ["sessionId"] = sessionId, ["configId"] = configId, ["value"] = value.DeepClone() };
        if (value is JsonValue v && v.TryGetValue<bool>(out _)) p["type"] = "boolean";
        var res = await RequestAsync("session/set_config_option", p);
        return res?["configOptions"] as JsonArray;
    }

    /// <summary>Legacy (unstable) model selection, for agents without config options.</summary>
    public Task SetModelAsync(string sessionId, string modelId) =>
        RequestAsync("session/set_model", new JsonObject { ["sessionId"] = sessionId, ["modelId"] = modelId });

    /// <summary>Legacy mode selection, for agents without config options.</summary>
    public Task SetModeAsync(string sessionId, string modeId) =>
        RequestAsync("session/set_mode", new JsonObject { ["sessionId"] = sessionId, ["modeId"] = modeId });

    public Task AuthenticateAsync(string methodId) => RequestAsync("authenticate", new JsonObject { ["methodId"] = methodId });

    public async Task<string> PromptAsync(string sessionId, JsonArray prompt)
    {
        var res = await RequestAsync("session/prompt", new JsonObject { ["sessionId"] = sessionId, ["prompt"] = prompt }, TimeSpan.FromHours(2));
        return res?["stopReason"]?.GetValue<string>() ?? "end_turn";
    }

    public void Cancel(string sessionId) => Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "session/cancel", ["params"] = new JsonObject { ["sessionId"] = sessionId } });

    // ---- JSON-RPC ----

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? p, TimeSpan? timeout = null)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? TimeSpan.FromMinutes(3)));
        if (done != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            throw new AcpError(-32603, $"Timed out waiting for {method}.");
        }
        return await tcs.Task;
    }

    private void Send(JsonObject msg)
    {
        string line = msg.ToJsonString();
        lock (_writeGate)
        {
            try { _stdin.WriteLine(line); }
            catch (Exception ex) { throw new AcpError(-32603, "Agent is not running: " + ex.Message); }
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (_proc.StandardOutput.ReadLine() is { } line)
            {
                if (line.Trim().Length == 0) continue;
                JsonObject? msg;
                try { msg = JsonNode.Parse(line) as JsonObject; }
                catch { StdErr?.Invoke("(non-JSON) " + line); continue; }
                if (msg == null) continue;

                string? method = msg["method"]?.GetValue<string>();
                JsonNode? id = msg["id"];
                if (method == null && id != null)
                {
                    if (id.GetValueKind() == System.Text.Json.JsonValueKind.Number && _pending.TryRemove(id.GetValue<long>(), out var tcs))
                    {
                        if (msg["error"] is JsonObject err)
                            tcs.TrySetException(new AcpError(err["code"]?.GetValue<int>() ?? -32603, err["message"]?.ToString() ?? "error", err["data"]?.DeepClone()));
                        else tcs.TrySetResult(msg["result"]?.DeepClone());
                    }
                }
                else if (method != null && id != null) _ = AnswerAsync(id.DeepClone(), method, msg["params"] as JsonObject);
                else if (method != null)
                {
                    try { Notification?.Invoke(method, msg["params"] as JsonObject); } catch { }
                }
            }
        }
        catch { /* process gone */ }
    }

    private async Task AnswerAsync(JsonNode id, string method, JsonObject? p)
    {
        JsonObject reply;
        try
        {
            if (RequestHandler == null) throw new AcpError(-32601, "Method not found: " + method);
            JsonNode? result = await RequestHandler(method, p);
            reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (AcpError e) { reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message } }; }
        catch (Exception e) { reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.Message } }; }
        try { Send(reply); } catch { }
    }

    public void Dispose()
    {
        try { _stdin.Close(); } catch { }
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        _proc.Dispose();
    }
}

public sealed class AcpError(int code, string message, JsonNode? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? ErrorData { get; } = data;
}
