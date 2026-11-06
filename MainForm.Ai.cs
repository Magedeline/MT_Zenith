using System.Text.Json.Nodes;
using CELMapViewer.Agent;

namespace CELMapViewer;

/// <summary>AI integration: the MCP server (map tools for any MCP client) and the ACP agent chat tab.</summary>
public sealed partial class MainForm
{
    private McpServer? _mcp;
    private MapTools? _mapTools;
    private AgentPanel _agentPanel = null!;
    private TabPage _aiPage = null!;
    private ToolStripMenuItem _mcpToggle = null!;

    private string McpUrl => $"http://localhost:{_settings.McpPort}/mcp";

    private void InitAi()
    {
        _mapTools = new MapTools(new GuiHost(this));
        if (_settings.McpAutoStart) Shown += (_, _) => StartMcp(quiet: true);
        FormClosed += (_, _) =>
        {
            _agentPanel.Disconnect();
            _mcp?.Dispose();
        };
    }

    private ToolStripMenuItem BuildAiMenu()
    {
        var ai = new ToolStripMenuItem("&AI");
        _mcpToggle = new ToolStripMenuItem($"&MCP server ({McpUrl})") { CheckOnClick = true, Checked = false };
        _mcpToggle.Click += (_, _) =>
        {
            if (_mcpToggle.Checked) StartMcp(quiet: false);
            else StopMcp();
            _settings.McpAutoStart = _mcp?.Running == true;
            _settings.Save();
        };
        var readOnly = new ToolStripMenuItem("AI is &read-only (no map edits)") { CheckOnClick = true, Checked = _settings.AiReadOnly };
        readOnly.CheckedChanged += (_, _) => { _settings.AiReadOnly = readOnly.Checked; _settings.Save(); };

        var copy = new ToolStripMenuItem("&Copy MCP client config");
        foreach (var (label, make) in ConfigSnippets())
            copy.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) =>
            {
                Clipboard.SetText(make());
                Log($"Copied {label} config to the clipboard.");
            }));

        ai.DropDownItems.Add(new ToolStripMenuItem("Agent &chat", null, (_, _) => _tabs.SelectedTab = _aiPage, Keys.Control | Keys.Shift | Keys.A));
        ai.DropDownItems.Add(new ToolStripSeparator());
        ai.DropDownItems.Add(_mcpToggle);
        ai.DropDownItems.Add(readOnly);
        ai.DropDownItems.Add(copy);
        return ai;
    }

    private TabPage BuildAiTab()
    {
        _aiPage = new TabPage("AI");
        _agentPanel = new AgentPanel(_settings) { Dock = DockStyle.Fill };
        _agentPanel.Log += s => { if (!IsDisposed) BeginInvoke(() => Log(s)); };
        _agentPanel.WorkingDirectory = () => GameLink.FindModDir() ?? (_path != null ? MapModel.ModDirOf(_path) : null) ?? Environment.CurrentDirectory;
        _agentPanel.Context = AgentContext;
        _agentPanel.McpServers = http =>
        {
            if (!StartMcp(quiet: true)) return new JsonArray();
            JsonObject server = http
                ? new JsonObject { ["type"] = "http", ["name"] = McpServer.ServerName, ["url"] = McpUrl, ["headers"] = new JsonArray() }
                : new JsonObject
                {
                    ["name"] = McpServer.ServerName, ["command"] = Environment.ProcessPath ?? Application.ExecutablePath,
                    ["args"] = new JsonArray("--mcp-bridge", McpUrl), ["env"] = new JsonArray()
                };
            return new JsonArray(server);
        };
        _aiPage.Controls.Add(_agentPanel);
        return _aiPage;
    }

    private string AgentContext()
    {
        var parts = new List<string> { $"[{AppInfo.Title} — the user is looking at this editor; use the {McpServer.ServerName} MCP tools to inspect and edit the map. Edits appear live and can be undone.]" };
        parts.Add(_path == null ? "No map is open." : $"Open map: {_path}{(_dirty ? " (unsaved changes)" : "")}.");
        if (_canvas.Selected is { } s)
        {
            parts.Add($"Selected room: {s.Room.Name}.");
            if (s.El != null) parts.Add($"Selected {s.Kind.ToLowerInvariant()}: {(s.Decal ? s.El.Str("texture") : s.El.Name)} id {s.El.Int("id")} at ({s.El.Num("x")},{s.El.Num("y")}).");
            if (_canvas.SelectedItems.Count > 1) parts.Add($"{_canvas.SelectedItems.Count} items are selected.");
        }
        if (_settings.AiReadOnly) parts.Add("The map is read-only for AI: do not try to edit it.");
        return string.Join(" ", parts);
    }

    private bool StartMcp(bool quiet)
    {
        if (_mcp?.Running == true) return true;
        try
        {
            _mcp ??= new McpServer(_mapTools!);
            _mcp.Log -= OnMcpLog;
            _mcp.Log += OnMcpLog;
            _mcp.StartHttp(_settings.McpPort);
            Log($"MCP server listening on {McpUrl}");
            _mcpToggle.Checked = true;
            return true;
        }
        catch (Exception ex)
        {
            _mcpToggle.Checked = false;
            string msg = $"Could not start the MCP server on port {_settings.McpPort}: {ex.Message} (is another viewer running?)";
            Log(msg);
            if (!quiet) MessageBox.Show(this, msg, "MCP server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void StopMcp()
    {
        _mcp?.Stop();
        _mcpToggle.Checked = false;
        Log("MCP server stopped.");
    }

    private void OnMcpLog(string s)
    {
        if (IsDisposed) return;
        try { BeginInvoke(() => _status.Text = s); } catch (InvalidOperationException) { }
    }

    private IEnumerable<(string Label, Func<string> Make)> ConfigSnippets()
    {
        string exe() => Environment.ProcessPath ?? Application.ExecutablePath;
        string json(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        yield return ("Claude Code (command)", () => $"claude mcp add --transport http {McpServer.ServerName} {McpUrl}");
        yield return ("VS Code / Visual Studio Copilot (.vscode/mcp.json)", () =>
            $"{{\n  \"servers\": {{\n    \"{McpServer.ServerName}\": {{ \"type\": \"http\", \"url\": \"{McpUrl}\" }}\n  }}\n}}");
        yield return ("GitHub Copilot CLI (~/.copilot/mcp-config.json)", () =>
            $"{{\n  \"mcpServers\": {{\n    \"{McpServer.ServerName}\": {{ \"type\": \"http\", \"url\": \"{McpUrl}\", \"tools\": [\"*\"] }}\n  }}\n}}");
        yield return ("Claude Desktop / Cursor / Windsurf (stdio bridge JSON)", () =>
            $"{{\n  \"mcpServers\": {{\n    \"{McpServer.ServerName}\": {{ \"command\": {json(exe())}, \"args\": [\"--mcp-bridge\", \"{McpUrl}\"] }}\n  }}\n}}");
        yield return ("OpenAI Codex (~/.codex/config.toml)", () =>
            $"[mcp_servers.{McpServer.ServerName}]\ncommand = '{exe()}'\nargs = [\"--mcp-bridge\", \"{McpUrl}\"]");
        yield return ("Headless, without this window (stdio JSON)", () =>
            $"{{\n  \"mcpServers\": {{\n    \"{McpServer.ServerName}\": {{ \"command\": {json(exe())}, \"args\": [\"--mcp\"] }}\n  }}\n}}");
    }

    /// <summary>Lets the map tools drive this window: edits land on the UI thread, with undo and a live redraw.</summary>
    private sealed class GuiHost(MainForm f) : IMapHost
    {
        public T Invoke<T>(Func<T> fn)
        {
            if (!f.InvokeRequired) return fn();
            T result = default!;
            Exception? error = null;
            f.Invoke(() =>
            {
                try { result = fn(); }
                catch (Exception ex) { error = ex; }
            });
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            return result;
        }

        public MapBin? Map => f._map;
        public string? Path => f._path;
        public bool Dirty => f._dirty;
        public bool ReadOnly => f._settings.AiReadOnly;
        public Autotiler? FgTiler => f._canvas.FgTiler;
        public Autotiler? BgTiler => f._canvas.BgTiler;
        public GameLink Game => f._game;

        public void Open(string path) => f.OpenCore(path);

        public void Save(string path)
        {
            if (f._map == null) throw new InvalidOperationException("No map is open.");
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            f._map.Save(path);
            f._path = System.IO.Path.GetFullPath(path);
            f._dirty = false;
            f.UpdateTitle();
            if (f._reloadOnSave.Checked && f._lastState != null) _ = f.ReloadGameAsync();
        }

        public void BeginEdit() => f.PushUndo();

        public void EndEdit(BinElement? level, string summary)
        {
            if (level == null)
            {
                f._canvas.Select(null);
                f.RebuildRooms(keepView: true);
            }
            else if (f._canvas.Rooms.FirstOrDefault(r => r.El == level) is { } room)
            {
                room.ReloadTiles();
                room.InvalidateRender();
                if (f._canvas.Selected is { El: { } el } s && s.Room == room &&
                    s.Container?.Children.Contains(el) != true)
                    f._canvas.Select(null);
                else f.LoadGrid();
                f._canvas.Invalidate();
            }
            else f.RebuildRooms(keepView: true);
            f.MarkDirty();
            f._status.Text = "AI: " + summary;
        }

        public bool Undo()
        {
            if (f._undo.Count == 0) return false;
            f.Undo();
            return true;
        }

        public void Log(string message)
        {
            if (f.IsDisposed) return;
            if (f.InvokeRequired) f.BeginInvoke(() => f.Log(message));
            else f.Log(message);
        }
    }
}
