using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

/// <summary>
/// Chat with an external coding agent over ACP. The agent gets the viewer's MCP tools, so it edits the open map
/// live (with undo) while the user watches.
/// </summary>
public sealed class AgentPanel : UserControl
{
    private readonly AppSettings _settings;
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _command = new() { Dock = DockStyle.Fill };
    private readonly Button _connect = new() { Text = "Connect", Dock = DockStyle.Fill, AutoSize = true };
    private readonly RichTextBox _out = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window,
        Font = new Font("Segoe UI", 9f), DetectUrls = true, HideSelection = false
    };
    private readonly TextBox _input = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = false,
        PlaceholderText = "Ask the agent… (Enter sends, Shift+Enter = new line)"
    };
    private readonly Button _send = new() { Text = "Send", Dock = DockStyle.Fill, Enabled = false };
    private readonly Button _stop = new() { Text = "Stop", Dock = DockStyle.Fill, Enabled = false };
    private readonly Button _attach = new() { Text = "Attach…", Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 0) };
    private readonly LinkLabel _attachments = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Color.DimGray };
    private readonly List<string> _files = new();
    private readonly Label _state = new() { Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft, Text = "Not connected." };
    private readonly FlowLayoutPanel _opts = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
    private readonly ComboBox _model = OptCombo(170), _effort = OptCombo(90), _mode = OptCombo(100);
    private readonly CheckBox _fast = new() { Text = "Fast", AutoSize = true, Margin = new Padding(6, 5, 3, 0) };
    private readonly CheckBox _thinking = new() { Text = "Thinking", AutoSize = true, Margin = new Padding(3, 5, 3, 0) };
    private readonly ComboBox _perm = OptCombo(175);
    private readonly CheckBox _strongest = new() { Text = "Strongest effort", Appearance = Appearance.Button, AutoSize = true, Margin = new Padding(8, 1, 3, 0) };
    private int _autoContinues;
    private const int MaxAutoContinues = 8;

    /// <summary>Whether a permission request's tool title names one of the editor's MCP tools (set by the host).</summary>
    public Func<string, bool>? IsMapTool;
    private readonly List<Control> _extraControls = new();
    private readonly ToolTip _tips = new();
    private readonly Font _bold, _italic;

    private JsonArray? _configOptions;
    private JsonObject? _legacyModels, _legacyModes;
    private bool _updating;
    private const string LegacyModel = "\u0001model", LegacyMode = "\u0001mode";

    private AcpClient? _client;
    private string? _session;
    private bool _busy;
    private Kind _last = Kind.None;
    private readonly Dictionary<string, string> _toolTitles = new();
    private readonly List<TaskCompletionSource<string?>> _openPermissions = new();

    /// <summary>Returns the ACP <c>mcpServers</c> list for a new session (arg: agent supports HTTP MCP).</summary>
    public Func<bool, JsonArray>? McpServers;
    /// <summary>A short description of the editor state, prepended to each prompt.</summary>
    public Func<string>? Context;
    public Func<string>? WorkingDirectory;
    public event Action<string>? Log;

    private enum Kind { None, User, Agent, Thought, Tool, Info }

    public AgentPanel(AppSettings settings)
    {
        _settings = settings;
        _bold = new Font(_out.Font, FontStyle.Bold);
        _italic = new Font(_out.Font, FontStyle.Italic);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 4, AutoSize = true, Padding = new Padding(2) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_preset, 0, 0);
        top.Controls.Add(_connect, 1, 0);
        top.Controls.Add(_command, 0, 1);
        top.SetColumnSpan(_command, 2);
        top.Controls.Add(_opts, 0, 2);
        top.SetColumnSpan(_opts, 2);
        top.Controls.Add(_state, 0, 3);
        top.SetColumnSpan(_state, 2);
        _state.Height = 18;
        BuildOptionsRow();

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 2, RowCount = 3, Height = 88, Padding = new Padding(2) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        bottom.Controls.Add(_attachments, 0, 0);
        bottom.Controls.Add(_attach, 1, 0);
        bottom.Controls.Add(_input, 0, 1);
        bottom.SetRowSpan(_input, 2);
        bottom.Controls.Add(_send, 1, 1);
        bottom.Controls.Add(_stop, 1, 2);
        UpdateAttachments();

        Controls.Add(_out);
        Controls.Add(bottom);
        Controls.Add(top);

        foreach (var p in settings.AgentPresets) _preset.Items.Add(p);
        int last = settings.AgentPresets.FindIndex(p => p.Name == settings.LastAgent);
        if (_preset.Items.Count > 0) _preset.SelectedIndex = Math.Max(0, last);
        _command.Text = (_preset.SelectedItem as AgentPreset)?.Command ?? "";
        _preset.SelectedIndexChanged += (_, _) => _command.Text = (_preset.SelectedItem as AgentPreset)?.Command ?? "";

        _connect.Click += async (_, _) => { if (_client == null) await ConnectAsync(); else Disconnect("Disconnected."); };
        _send.Click += async (_, _) => await SendAsync();
        _stop.Click += (_, _) => CancelTurn();
        _attach.Click += (_, _) => BrowseAttachments();
        _attachments.LinkClicked += (_, _) => { _files.Clear(); UpdateAttachments(); };
        foreach (Control c in new Control[] { _input, _out })
        {
            c.AllowDrop = true;
            c.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f) AddAttachments(f); };
        }
        _input.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.V && e.Control && PasteAttachment()) e.SuppressKeyPress = true;
            else if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                if (_send.Enabled) await SendAsync();
            }
        };

        Append("Pick an agent and press Connect. The agent runs on this PC with your own login/API key and gets the " +
               "viewer's map tools (MCP), so it can inspect, render and edit the open map; you see each change live and can Ctrl+Z it.\n" +
               "Agents must be installed first, e.g. `npm i -g @github/copilot`, `npm i -g @google/gemini-cli`, `npm i -g opencode-ai` " +
               "(npx presets download on first use).\n" +
               "Strongest effort picks the agent's most capable model and highest effort, auto-allows the map tools, adds a build-and-verify directive " +
               "to every prompt and continues automatically when a turn limit stops the agent: use it with Design ▸ Build a complete level.\n" +
               "Attach images, audio or video (Attach…, drag & drop, or paste a screenshot with Ctrl+V): the agent sees images directly and gets " +
               "waveform/spectrogram figures, video contact sheets and statistics for audio/video.\n", Kind.Info);
    }

    // ---- connection ----

    private async Task ConnectAsync()
    {
        string cmd = _command.Text.Trim();
        if (cmd.Length == 0) return;
        if (_preset.SelectedItem is AgentPreset p)
        {
            p.Command = cmd;
            _settings.LastAgent = p.Name;
            _settings.Save();
        }
        string cwd = WorkingDirectory?.Invoke() ?? Environment.CurrentDirectory;
        SetState($"Starting {cmd}…");
        _connect.Enabled = false;
        AcpClient? client = null;
        try
        {
            client = AcpClient.Start(cmd, cwd);
            client.Notification += (m, prm) => Ui(() => OnNotification(m, prm));
            client.StdErr += line => Log?.Invoke("[agent] " + line);
            client.Exited += () => Ui(() => { if (_client == client) Disconnect("The agent process exited (see the log below for its output)."); });
            client.RequestHandler = HandleRequestAsync;
            _client = client;

            await client.InitializeAsync();
            string agentName = client.AgentInfo?["title"]?.ToString() ?? client.AgentInfo?["name"]?.ToString() ?? cmd;
            var servers = McpServers?.Invoke(client.SupportsHttpMcp) ?? new JsonArray();
            JsonObject session;
            try { session = await client.NewSessionAsync(cwd, servers); }
            catch (AcpError e) when (e.Code == -32000 && client.AuthMethods is { Count: > 0 } methods)
            {
                // Authentication required: try the agent's first method (most open a browser login).
                string method = methods[0]?["id"]?.ToString() ?? "";
                Append($"{agentName} needs you to sign in ({methods[0]?["name"]}). Trying…\n", Kind.Info);
                await client.AuthenticateAsync(method);
                session = await client.NewSessionAsync(cwd, McpServers?.Invoke(client.SupportsHttpMcp) ?? new JsonArray());
            }
            _session = session["sessionId"]!.GetValue<string>();
            _configOptions = session["configOptions"]?.DeepClone() as JsonArray;
            _legacyModels = _configOptions == null ? session["models"]?.DeepClone() as JsonObject : null;
            _legacyModes = _configOptions == null ? session["modes"]?.DeepClone() as JsonObject : null;
            RebuildOptions();
            SetState($"Connected: {agentName}");
            Append($"Connected to {agentName}. Map tools are available as the \"{McpServer.ServerName}\" MCP server.\n", Kind.Info);
            _connect.Text = "Disconnect";
            UpdateButtons();
            await ApplyPreferencesAsync();
            if (_strongest.Checked) await ApplyStrongestAsync();
            if (DescribeOptions() is { Length: > 0 } od) Append(od + "\n", Kind.Info);
            _input.Focus();
        }
        catch (Exception ex)
        {
            string msg = ex is AcpError { Code: -32000 } && client?.AuthMethods is { Count: > 0 } am
                ? $"Sign-in required. Log in with the agent's own CLI in a terminal first ({string.Join(", ", am.Select(m => m?["name"]?.ToString()))}), then reconnect."
                : ex.Message;
            Disconnect("Could not connect: " + msg);
        }
        finally { _connect.Enabled = true; }
    }

    public void Disconnect(string? why = null)
    {
        var c = _client;
        _client = null;
        _session = null;
        _busy = false;
        _configOptions = null;
        _legacyModels = _legacyModes = null;
        RebuildOptions();
        foreach (var tcs in _openPermissions.ToArray()) tcs.TrySetResult(null);
        try { c?.Dispose(); } catch { }
        _connect.Text = "Connect";
        SetState(why ?? "Not connected.");
        if (why != null) Append(why + "\n", Kind.Info);
        UpdateButtons();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _client?.Dispose(); } catch { }
            _client = null;
            _bold.Dispose();
            _italic.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- session options (ACP configOptions: model, effort, mode, fast, thinking, …) ----

    private sealed record Item(string Value, string Name, string? Description)
    {
        public override string ToString() => Name;
    }

    private sealed class Opt
    {
        public required string Id, Name, Type;
        public string? Category, Description;
        public JsonNode? Current;
        public List<Item> Values = new();
        public string CurrentText => Current?.ToString() ?? "";
        public bool CurrentBool => Current is JsonValue v && v.TryGetValue<bool>(out bool b) && b;
        public bool Has(params string[] words) =>
            words.Any(w => Id.Contains(w, StringComparison.OrdinalIgnoreCase) || Name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record Slots(Opt? Model, Opt? Effort, Opt? Mode, Opt? Fast, Opt? Thinking, List<Opt> Extras);

    private static ComboBox OptCombo(int width) =>
        new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(0, 2, 6, 2) };

    private static Label OptLabel(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 1, 0) };

    /// <summary>"Design ▾": fills the input with a level-design workflow (whole level, rooms, PCG, review, polish) for a difficulty tier.</summary>
    private Button BuildDesignButton()
    {
        var menu = new ContextMenuStrip();
        var build = new ToolStripMenuItem("Build a complete level (strongest effort)");
        var design = new ToolStripMenuItem("Design a few rooms for");
        var pcg = new ToolStripMenuItem("Generate with PCG and refine for");
        var review = new ToolStripMenuItem("Review open map against");
        for (int t = 1; t <= 5; t++)
        {
            int tier = t;
            string name = ReferenceMaps.TierNames[tier];
            build.DropDownItems.Add(name, null, (_, _) => { if (!_strongest.Checked) _strongest.Checked = true; UsePrompt(Prompts.BuildMap(tier, name, null, null, null, null)); });
            design.DropDownItems.Add(name, null, (_, _) => UsePrompt(Prompts.DesignLevel(tier, name, null, null)));
            pcg.DropDownItems.Add(name, null, (_, _) => UsePrompt(Prompts.PcgLevel(tier, name, null, null)));
            review.DropDownItems.Add(name, null, (_, _) => UsePrompt(Prompts.ReviewDifficulty(tier, name)));
        }
        menu.Items.Add(build);
        menu.Items.Add(design);
        menu.Items.Add(pcg);
        menu.Items.Add(review);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Polish and finish the open map", null, (_, _) => UsePrompt(Prompts.PolishMap()));
        var btn = new Button { Text = "Design ▾", AutoSize = true, Margin = new Padding(8, 1, 3, 0) };
        btn.Click += (_, _) => menu.Show(btn, new Point(0, btn.Height));
        _tips.SetToolTip(btn, "Insert a level-design prompt for a difficulty tier (Beginner … Grandmaster): a whole verified level, a few rooms, a PCG draft to refine, " +
                              "a difficulty review or a polish pass. The agent studies installed collab maps, builds with build_room/batch, verifies with analyze_room/analyze_map.");
        return btn;
    }

    // ---- strongest effort: best model, max effort, thinking on, fast off ----

    private static readonly string[] EffortOrder = { "max", "xhigh", "ultra", "highest", "very high", "very_high", "veryhigh", "high", "medium" };

    /// <summary>Capability ranking by model name: bigger/newer first, speed-tier models last.</summary>
    internal static int RankModel(string value, string name)
    {
        string s = (value + " " + name).ToLowerInvariant();
        int score = 10;
        foreach (var (word, w) in new[]
        {
            ("mythos", 100), ("fable", 98), ("opus", 95), ("gpt-5", 90), ("o3", 85), ("gemini", 60), ("gpt-4", 70), ("o1", 78),
            ("codex", 72), ("grok", 70), ("sonnet", 70), ("deepseek", 60), ("pro", 15), ("default", 40), ("recommended", 5), ("latest", 3),
            ("ultra", 12), ("max", 8), ("thinking", 6), ("reasoning", 6), ("1m", 2)
        })
            if (s.Contains(word)) score += w;
        foreach (string slow in new[] { "haiku", "flash", "mini", "nano", "lite", "fast", "small", "turbo", "instant" })
            if (s.Contains(slow)) score -= 45;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)(?:[.-](\d+))?");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int major) && major < 100)
            score += Math.Min(30, major * 3) + (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out int minor) ? Math.Min(9, minor) : 0);
        return score;
    }

    private static Item? PickEffort(IEnumerable<Item> items)
    {
        var list = items.ToList();
        foreach (string want in EffortOrder)
            if (list.FirstOrDefault(i => i.Value.Contains(want, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(want, StringComparison.OrdinalIgnoreCase)) is { } hit)
                return hit;
        return null;
    }

    /// <summary>Switches the session to the most capable model and highest effort the agent offers, thinking on, fast off.</summary>
    private async Task ApplyStrongestAsync()
    {
        if (_client == null || _session == null) return;
        var changed = new List<string>();
        var s = Classify();
        if (s.Model is { } m && m.Values.Count > 0)
        {
            var best = m.Values.OrderByDescending(v => RankModel(v.Value, v.Name)).First();
            if (best.Value != m.CurrentText) { _settings.AgentModel = best.Value; await SetOptionAsync(m, JsonValue.Create(best.Value)); changed.Add("model " + best.Name); }
        }
        s = Classify();
        if (s.Effort is { } e && PickEffort(e.Values) is { } eff && eff.Value != e.CurrentText)
        {
            _settings.AgentEffort = eff.Value;
            await SetOptionAsync(e, JsonValue.Create(eff.Value));
            changed.Add("effort " + eff.Name);
        }
        s = Classify();
        if (s.Fast is { } f && ToggleState(f, fast: true)) { await SetToggleAsync(f, false, fast: true); changed.Add("fast off"); }
        s = Classify();
        if (s.Thinking is { } t && !ToggleState(t, fast: false)) { await SetToggleAsync(t, true, fast: false); changed.Add("thinking on"); }
        _settings.AgentFast = false;
        _settings.AgentThinking = true;
        _settings.Save();
        Append(changed.Count > 0 ? "Strongest effort: " + string.Join(", ", changed) + ".\n" : "Strongest effort: the agent already runs its strongest options (or offers none to switch).\n", Kind.Info);
    }

    private static readonly (string Id, string Label)[] PermissionPolicies =
    {
        ("ask", "Permissions: ask"), ("map", "Permissions: auto-allow map tools"), ("all", "Permissions: auto-allow all")
    };

    /// <summary>The option to pick automatically for a permission request under the current policy, or null to ask.</summary>
    private string? AutoPermission(JsonObject? p)
    {
        string policy = _settings.AgentPermissions;
        if (policy == "ask" || p?["options"] is not JsonArray options || options.Count == 0) return null;
        var call = p["toolCall"] as JsonObject;
        string title = call?["title"]?.ToString() ?? "";
        string raw = call?["rawInput"]?.ToJsonString() ?? "";
        bool isMap = title.Contains(McpServer.ServerName, StringComparison.OrdinalIgnoreCase) || raw.Contains(McpServer.ServerName, StringComparison.OrdinalIgnoreCase)
                     || IsMapTool?.Invoke(title) == true;
        if (policy == "map" && !isMap) return null;
        string? Pick(Func<JsonNode?, bool> f) => options.FirstOrDefault(f)?["optionId"]?.ToString();
        return Pick(o => o?["kind"]?.ToString() == "allow_always")
               ?? Pick(o => o?["kind"]?.ToString() == "allow_once")
               ?? Pick(o => (o?["name"]?.ToString() ?? "").Contains("allow", StringComparison.OrdinalIgnoreCase) || (o?["name"]?.ToString() ?? "").Contains("yes", StringComparison.OrdinalIgnoreCase));
    }

    private void UsePrompt(string text)
    {
        _input.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _input.SelectionStart = _input.TextLength;
        _input.Focus();
    }

    private void BuildOptionsRow()
    {
        _opts.Controls.Add(OptLabel("Model"));
        _opts.Controls.Add(_model);
        _opts.Controls.Add(OptLabel("Effort"));
        _opts.Controls.Add(_effort);
        _opts.Controls.Add(OptLabel("Mode"));
        _opts.Controls.Add(_mode);
        _opts.Controls.Add(_fast);
        _opts.Controls.Add(_thinking);
        _opts.Controls.Add(_perm);
        _opts.Controls.Add(_strongest);
        _opts.Controls.Add(BuildDesignButton());

        foreach (var (_, label) in PermissionPolicies) _perm.Items.Add(label);
        _perm.SelectedIndex = Math.Max(0, Array.FindIndex(PermissionPolicies, pp => pp.Id == _settings.AgentPermissions));
        _perm.SelectedIndexChanged += (_, _) =>
        {
            if (_perm.SelectedIndex < 0) return;
            _settings.AgentPermissions = PermissionPolicies[_perm.SelectedIndex].Id;
            _settings.Save();
        };
        _tips.SetToolTip(_perm, "How to answer the agent's permission requests. 'Auto-allow map tools' approves the mt-zenith MCP tools (map edits are undoable) " +
                                "and still asks for file writes and shell commands; 'auto-allow all' approves everything.");
        _strongest.Checked = _settings.AgentMaxEffort;
        _strongest.CheckedChanged += async (_, _) =>
        {
            _settings.AgentMaxEffort = _strongest.Checked;
            if (_strongest.Checked && _settings.AgentPermissions == "ask")
            {
                _settings.AgentPermissions = "map";
                _updating = true;
                _perm.SelectedIndex = 1;
                _updating = false;
            }
            _settings.Save();
            if (_strongest.Checked && _client != null) await ApplyStrongestAsync();
            else if (!_strongest.Checked) Append("Strongest effort off.\n", Kind.Info);
        };
        _tips.SetToolTip(_strongest, "Strongest effort: switch to the agent's most capable model and highest effort (thinking on, fast off), auto-allow the map tools, " +
                                     "prepend a build-and-verify directive to every prompt and continue automatically when a turn/token limit stops the agent (up to 8 times).");

        foreach (var cb in new[] { _model, _effort, _mode })
        {
            var combo = cb;
            combo.SelectedIndexChanged += async (_, _) =>
            {
                if (_updating || combo.Tag is not Opt o || combo.SelectedItem is not Item it || it.Value == o.CurrentText) return;
                if (combo == _model) _settings.AgentModel = it.Value;
                else if (combo == _effort) _settings.AgentEffort = it.Value;
                else if (combo == _mode) _settings.AgentMode = it.Value;
                _settings.Save();
                await SetOptionAsync(o, JsonValue.Create(it.Value));
            };
        }
        _fast.CheckedChanged += async (_, _) =>
        {
            if (_updating) return;
            _settings.AgentFast = _fast.Checked;
            _settings.Save();
            if (_fast.Tag is Opt o) await SetToggleAsync(o, _fast.Checked, fast: true);
        };
        _thinking.CheckedChanged += async (_, _) =>
        {
            if (_updating) return;
            _settings.AgentThinking = _settings.ShowThoughts = _thinking.Checked;
            _settings.Save();
            if (_thinking.Tag is Opt o) await SetToggleAsync(o, _thinking.Checked, fast: false);
        };
        RebuildOptions();
    }

    private List<Opt> ParseOptions()
    {
        var list = new List<Opt>();
        if (_configOptions != null)
            foreach (var n in _configOptions)
            {
                if (n is not JsonObject o || o["id"]?.ToString() is not { Length: > 0 } id) continue;
                string type = o["type"]?.ToString() ?? "";
                if (type is not ("select" or "boolean")) continue; // unknown types must be ignored
                var opt = new Opt
                {
                    Id = id, Name = o["name"]?.ToString() ?? id, Type = type, Category = o["category"]?.ToString(),
                    Description = o["description"]?.ToString(), Current = o["currentValue"]
                };
                if (o["options"] is JsonArray values)
                    foreach (var v in values)
                    {
                        if (v?["options"] is JsonArray group) // grouped: { group, name, options: [...] }
                            foreach (var g in group) AddItem(opt, g, v["name"]?.ToString());
                        else AddItem(opt, v, null);
                    }
                list.Add(opt);
            }
        if (_legacyModels?["availableModels"] is JsonArray models)
            list.Add(new Opt
            {
                Id = LegacyModel, Name = "Model", Type = "select", Category = "model", Current = _legacyModels["currentModelId"],
                Values = models.Select(m => new Item(m?["modelId"]?.ToString() ?? "", m?["name"]?.ToString() ?? m?["modelId"]?.ToString() ?? "", m?["description"]?.ToString()))
                    .Where(i => i.Value.Length > 0).ToList()
            });
        if (_legacyModes?["availableModes"] is JsonArray modes)
            list.Add(new Opt
            {
                Id = LegacyMode, Name = "Mode", Type = "select", Category = "mode", Current = _legacyModes["currentModeId"],
                Values = modes.Select(m => new Item(m?["id"]?.ToString() ?? "", m?["name"]?.ToString() ?? m?["id"]?.ToString() ?? "", m?["description"]?.ToString()))
                    .Where(i => i.Value.Length > 0).ToList()
            });
        return list;

        static void AddItem(Opt opt, JsonNode? v, string? group)
        {
            if (v?["value"]?.ToString() is not { Length: > 0 } value) return;
            string name = v["name"]?.ToString() ?? value;
            opt.Values.Add(new Item(value, group != null ? $"{group} · {name}" : name, v["description"]?.ToString()));
        }
    }

    /// <summary>Maps the agent's options onto the fixed controls; categories are only hints, so ids/names are matched too.</summary>
    private Slots Classify()
    {
        var all = ParseOptions();
        var left = new List<Opt>(all);
        Opt? Take(Func<Opt, bool> f)
        {
            var o = left.FirstOrDefault(f);
            if (o != null) left.Remove(o);
            return o;
        }
        var model = Take(o => o.Type == "select" && (o.Category == "model" || o.Id.Equals("model", StringComparison.OrdinalIgnoreCase)));
        var mode = Take(o => o.Type == "select" && (o.Category == "mode" || o.Id.Equals("mode", StringComparison.OrdinalIgnoreCase)));
        var thinking = Take(o => o.Has("think") && (o.Type == "boolean" || (o.Values.Count == 2 && o.Values.Any(v => IsOff(v.Value) || IsOff(v.Name)))));
        var effort = Take(o => o.Type == "select" && (o.Category == "thought_level" || o.Has("effort", "reasoning", "think")));
        var fast = Take(o => o.Has("fast", "speed", "turbo") && (o.Type == "boolean" || o.Values.Any(v => IsFast(v.Value) || IsFast(v.Name))));
        thinking ??= Take(o => o.Type == "boolean" && o.Category == "thought_level");
        return new Slots(model, effort, mode, fast, thinking, left);
    }

    private static bool IsFast(string s) =>
        s.Contains("fast", StringComparison.OrdinalIgnoreCase) || s.Contains("turbo", StringComparison.OrdinalIgnoreCase);

    private static bool IsOff(string s) =>
        s.Equals("off", StringComparison.OrdinalIgnoreCase) || s.Equals("no", StringComparison.OrdinalIgnoreCase)
        || s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Contains("none", StringComparison.OrdinalIgnoreCase)
        || s.Contains("disable", StringComparison.OrdinalIgnoreCase);

    private static bool ToggleState(Opt o, bool fast) =>
        o.Type == "boolean" ? o.CurrentBool : fast ? IsFast(o.CurrentText) : !IsOff(o.CurrentText);

    /// <summary>The select value that turns a fast/thinking option on or off (null = the agent offers none).</summary>
    private static string? ToggleValue(Opt o, bool on, bool fast)
    {
        bool Matches(Item i) => fast ? IsFast(i.Value) || IsFast(i.Name) : !(IsOff(i.Value) || IsOff(i.Name));
        var pool = o.Values.Where(i => Matches(i) == on).ToList();
        if (pool.Count == 0) return null;
        if (pool.Any(i => i.Value == o.CurrentText)) return o.CurrentText;
        if (!on && fast)
            return (pool.FirstOrDefault(i => new[] { "standard", "default", "normal", "quality" }.Any(w => i.Value.Contains(w, StringComparison.OrdinalIgnoreCase))) ?? pool[0]).Value;
        return pool[0].Value;
    }

    private void RebuildOptions()
    {
        if (IsDisposed) return;
        _updating = true;
        _opts.SuspendLayout();
        try
        {
            var s = Classify();
            bool connected = _client != null && _session != null;
            BindCombo(_model, s.Model, connected, "Model: the agent did not offer a model list. Pick the model with the agent's own CLI flag in the command box (e.g. --model).");
            BindCombo(_effort, s.Effort, connected, "Effort / reasoning level: not offered by this agent.");
            BindCombo(_mode, s.Mode, connected, "Mode: not offered by this agent.");
            BindToggle(_fast, s.Fast, connected, fast: true,
                "Fast mode: not offered by this agent (pick a faster model instead).");
            if (s.Thinking != null) BindToggle(_thinking, s.Thinking, connected, fast: false, "");
            else
            {
                _thinking.Tag = null;
                _thinking.Enabled = true;
                _thinking.Checked = _settings.ShowThoughts;
                _tips.SetToolTip(_thinking, "Show the agent's thinking in the chat. (This agent has no thinking switch; use Effort if it offers one.)");
            }

            foreach (var c in _extraControls) { _opts.Controls.Remove(c); c.Dispose(); }
            _extraControls.Clear();
            if (connected)
                foreach (var o in s.Extras)
                {
                    var opt = o;
                    if (o.Type == "boolean")
                    {
                        var cb = new CheckBox { Text = o.Name, AutoSize = true, Checked = o.CurrentBool, Margin = new Padding(6, 5, 3, 0), Tag = o };
                        cb.CheckedChanged += async (_, _) => { if (!_updating) await SetOptionAsync(opt, JsonValue.Create(cb.Checked)); };
                        _tips.SetToolTip(cb, o.Description ?? o.Name);
                        _extraControls.Add(cb);
                    }
                    else
                    {
                        var label = OptLabel(o.Name);
                        var combo = OptCombo(110);
                        combo.Items.AddRange(o.Values.Cast<object>().ToArray());
                        combo.SelectedItem = o.Values.FirstOrDefault(v => v.Value == o.CurrentText);
                        combo.SelectedIndexChanged += async (_, _) =>
                        {
                            if (!_updating && combo.SelectedItem is Item it && it.Value != opt.CurrentText) await SetOptionAsync(opt, JsonValue.Create(it.Value));
                        };
                        _tips.SetToolTip(combo, o.Description ?? o.Name);
                        _extraControls.Add(label);
                        _extraControls.Add(combo);
                    }
                }
            foreach (var c in _extraControls) _opts.Controls.Add(c);
        }
        finally
        {
            _opts.ResumeLayout();
            _updating = false;
        }
    }

    private void BindCombo(ComboBox cb, Opt? o, bool connected, string missingTip)
    {
        cb.Items.Clear();
        cb.Tag = o;
        if (o == null)
        {
            cb.Items.Add(connected ? "(agent default)" : "(connect first)");
            cb.SelectedIndex = 0;
            cb.Enabled = false;
            _tips.SetToolTip(cb, connected ? missingTip : "Connect to an agent to see its options.");
            return;
        }
        cb.Items.AddRange(o.Values.Cast<object>().ToArray());
        var cur = o.Values.FirstOrDefault(v => v.Value == o.CurrentText);
        if (cur == null && o.CurrentText.Length > 0) { cur = new Item(o.CurrentText, o.CurrentText, null); cb.Items.Insert(0, cur); }
        cb.SelectedItem = cur;
        cb.Enabled = true;
        _tips.SetToolTip(cb, (o.Description ?? o.Name) + (cur?.Description is { Length: > 0 } d ? "\n" + cur.Name + ": " + d : ""));
    }

    private void BindToggle(CheckBox cb, Opt? o, bool connected, bool fast, string missingTip)
    {
        cb.Tag = o;
        if (o == null)
        {
            cb.Checked = false;
            cb.Enabled = false;
            _tips.SetToolTip(cb, connected ? missingTip : "Connect to an agent to see its options.");
            return;
        }
        cb.Enabled = true;
        cb.Checked = ToggleState(o, fast);
        _tips.SetToolTip(cb, $"{o.Name}{(o.Description is { Length: > 0 } d ? ": " + d : "")}");
    }

    private async Task SetToggleAsync(Opt o, bool on, bool fast)
    {
        if (o.Type == "boolean") { await SetOptionAsync(o, JsonValue.Create(on)); return; }
        if (ToggleValue(o, on, fast) is { } v) { if (v != o.CurrentText) await SetOptionAsync(o, JsonValue.Create(v)); }
        else RebuildOptions(); // nothing to switch to: snap the checkbox back
    }

    private async Task SetOptionAsync(Opt o, JsonNode value)
    {
        var client = _client;
        string? sid = _session;
        if (client == null || sid == null) return;
        try
        {
            if (o.Id == LegacyModel)
            {
                await client.SetModelAsync(sid, value.ToString());
                if (_legacyModels != null) _legacyModels["currentModelId"] = value.ToString();
            }
            else if (o.Id == LegacyMode)
            {
                await client.SetModeAsync(sid, value.ToString());
                if (_legacyModes != null) _legacyModes["currentModeId"] = value.ToString();
            }
            else
            {
                var list = await client.SetConfigOptionAsync(sid, o.Id, value);
                if (list != null) _configOptions = (JsonArray)list.DeepClone();
                else if (_configOptions?.OfType<JsonObject>().FirstOrDefault(x => x["id"]?.ToString() == o.Id) is { } local)
                    local["currentValue"] = value.DeepClone();
            }
            Log?.Invoke($"[agent] {o.Name} = {value}");
        }
        catch (Exception ex)
        {
            Append($"Could not change {o.Name}: {ex.Message}\n", Kind.Info);
        }
        if (_client == client) RebuildOptions();
    }

    /// <summary>Re-applies the user's saved model/effort/mode/fast/thinking choices when this agent offers them.</summary>
    private async Task ApplyPreferencesAsync()
    {
        async Task Pick(Func<Slots, Opt?> slot, string? want)
        {
            if (want == null || slot(Classify()) is not { } o || o.CurrentText == want || o.Values.All(v => v.Value != want)) return;
            await SetOptionAsync(o, JsonValue.Create(want));
        }
        async Task Toggle(Func<Slots, Opt?> slot, bool? want, bool fast)
        {
            if (want is not { } on || slot(Classify()) is not { } o || ToggleState(o, fast) == on) return;
            await SetToggleAsync(o, on, fast);
        }
        await Pick(s => s.Model, _settings.AgentModel);
        await Pick(s => s.Mode, _settings.AgentMode);
        await Pick(s => s.Effort, _settings.AgentEffort);
        await Toggle(s => s.Fast, _settings.AgentFast, fast: true);
        await Toggle(s => s.Thinking, _settings.AgentThinking, fast: false);
    }

    private string DescribeOptions()
    {
        var s = Classify();
        var parts = new List<string>();
        void Add(string label, Opt? o)
        {
            if (o == null) return;
            string v = o.Type == "boolean" ? (o.CurrentBool ? "on" : "off") : o.Values.FirstOrDefault(i => i.Value == o.CurrentText)?.Name ?? o.CurrentText;
            parts.Add($"{label}: {v}");
        }
        Add("Model", s.Model);
        Add("Effort", s.Effort);
        Add("Mode", s.Mode);
        Add("Fast", s.Fast);
        Add("Thinking", s.Thinking);
        foreach (var o in s.Extras) Add(o.Name, o);
        return parts.Count == 0 ? "This agent exposes no session options (model, effort, …); set them with its CLI flags in the command box." : string.Join(" · ", parts);
    }

    // ---- prompting ----

    private async Task SendAsync()
    {
        string text = _input.Text.Trim();
        if ((text.Length == 0 && _files.Count == 0) || _client == null || _session == null || _busy) return;
        if (text.Length == 0) text = "Look at the attached file(s).";
        var files = _files.ToList();
        _files.Clear();
        UpdateAttachments();
        _input.Clear();
        Append(text + "\n" + string.Concat(files.Select(f => "📎 " + Path.GetFileName(f) + "\n")), Kind.User);
        var prompt = ContextBlocks();
        prompt.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        _busy = true;
        _autoContinues = 0;
        UpdateButtons();
        try
        {
            if (files.Count > 0)
            {
                SetState("Preparing attachments…");
                var caps = _client.AgentCapabilities?["promptCapabilities"];
                bool img = caps?["image"]?.GetValue<bool>() == true, aud = caps?["audio"]?.GetValue<bool>() == true;
                foreach (var block in await Task.Run(() => AgentAttachments.Build(files, img, aud)))
                    prompt.Add(block!.DeepClone());
                if (!img) Append("(This agent doesn't accept images, so attachments are sent as statistics and file links.)\n", Kind.Info);
            }
            SetState("Working…");
            string stop = await _client.PromptAsync(_session, prompt);
            // Strongest effort: a turn or token limit is not the end of the task; pick it up where it stopped.
            while (_strongest.Checked && stop is "max_turn_requests" or "max_tokens" && _autoContinues < MaxAutoContinues && _client != null && _session != null)
            {
                _autoContinues++;
                Append($"\nLimit reached ({stop}); continuing automatically ({_autoContinues}/{MaxAutoContinues}).\n", Kind.Info);
                _last = Kind.None;
                var cont = ContextBlocks();
                cont.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "Continue the task from exactly where you stopped. First call design_notes read (todo) and get_status so you know the current state; " +
                               "do not redo finished work; finish the remaining plan, verify, and report."
                });
                SetState($"Working… (auto-continue {_autoContinues})");
                stop = await _client.PromptAsync(_session, cont);
            }
            SetState(stop switch
            {
                "end_turn" => "Ready.",
                "cancelled" => "Stopped.",
                "max_tokens" => "Stopped: token limit reached.",
                "max_turn_requests" => "Stopped: turn limit reached.",
                "refusal" => "The agent refused.",
                _ => "Ready (" + stop + ")."
            });
        }
        catch (Exception ex)
        {
            Append("Error: " + ex.Message + "\n", Kind.Info);
            SetState("Error.");
        }
        finally
        {
            _busy = false;
            _last = Kind.None;
            UpdateButtons();
        }
    }

    /// <summary>The editor-state context (and, in strongest mode, the autonomy directive) that opens every prompt.</summary>
    private JsonArray ContextBlocks()
    {
        var blocks = new JsonArray();
        if (Context?.Invoke() is { Length: > 0 } ctx) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = ctx });
        if (_strongest.Checked) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = Prompts.StrongestDirective });
        return blocks;
    }

    // ---- attachments ----

    private void BrowseAttachments()
    {
        static string Pat(IEnumerable<string> exts) => string.Join(";", exts.Select(e => "*" + e));
        using var dlg = new OpenFileDialog
        {
            Multiselect = true, Title = "Attach files for the agent",
            Filter = $"Media|{Pat(Media.ImageExts.Concat(Media.AudioExts).Concat(Media.VideoExts))}|Images|{Pat(Media.ImageExts)}|" +
                     $"Audio|{Pat(Media.AudioExts)}|Video|{Pat(Media.VideoExts)}|All files|*.*"
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddAttachments(dlg.FileNames);
    }

    private void AddAttachments(IEnumerable<string> paths)
    {
        foreach (string p in paths)
            if (File.Exists(p) && !_files.Contains(p, StringComparer.OrdinalIgnoreCase)) _files.Add(Path.GetFullPath(p));
        UpdateAttachments();
    }

    /// <summary>Ctrl+V with an image or files on the clipboard attaches them (images are saved as a temp PNG).</summary>
    private bool PasteAttachment()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var list = Clipboard.GetFileDropList().Cast<string>().Where(f => Media.KindOf(f) != null).ToList();
                if (list.Count == 0) return false;
                AddAttachments(list);
                return true;
            }
            if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } img) return false;
            using (img)
            {
                string dir = Path.Combine(Path.GetTempPath(), AppInfo.Name);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"pasted-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                img.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                AddAttachments(new[] { file });
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException) { return false; }
    }

    private void UpdateAttachments()
    {
        if (_files.Count == 0)
        {
            _attachments.Text = "No attachments (drop or paste images/audio/video)";
            _attachments.LinkArea = new LinkArea(0, 0);
            _tips.SetToolTip(_attachments, null);
            return;
        }
        string names = string.Join(", ", _files.Select(Path.GetFileName));
        string head = $"📎 {_files.Count} ";
        _attachments.Text = $"{head}[clear]  {names}";
        _attachments.LinkArea = new LinkArea(head.Length, "[clear]".Length);
        _tips.SetToolTip(_attachments, string.Join("\n", _files));
    }

    private void CancelTurn()
    {
        if (_client == null || _session == null) return;
        foreach (var tcs in _openPermissions.ToArray()) tcs.TrySetResult(null);
        try { _client.Cancel(_session); } catch { }
        SetState("Stopping…");
    }

    private void UpdateButtons()
    {
        bool ready = _client != null && _session != null;
        _send.Enabled = ready && !_busy;
        _stop.Enabled = ready && _busy;
        _preset.Enabled = _command.Enabled = _client == null;
    }

    // ---- agent → client ----

    private void OnNotification(string method, JsonObject? p)
    {
        if (method != "session/update" || p?["update"] is not JsonObject u) return;
        switch (u["sessionUpdate"]?.ToString())
        {
            case "agent_message_chunk":
                if (TextOf(u["content"]) is { } msg) Append(msg, Kind.Agent);
                break;
            case "agent_thought_chunk":
                if (_thinking.Checked && TextOf(u["content"]) is { } th) Append(th, Kind.Thought);
                break;
            case "config_option_update":
                if (u["configOptions"] is JsonArray co) { _configOptions = (JsonArray)co.DeepClone(); RebuildOptions(); }
                break;
            case "current_mode_update":
                if (_legacyModes != null && u["currentModeId"]?.ToString() is { } cm) { _legacyModes["currentModeId"] = cm; RebuildOptions(); }
                break;
            case "tool_call":
            {
                string id = u["toolCallId"]?.ToString() ?? "";
                string title = u["title"]?.ToString() ?? "tool";
                _toolTitles[id] = title;
                Append($"⚙ {title}\n", Kind.Tool);
                break;
            }
            case "tool_call_update":
            {
                string id = u["toolCallId"]?.ToString() ?? "";
                if (u["title"]?.ToString() is { Length: > 0 } t) _toolTitles[id] = t;
                string status = u["status"]?.ToString() ?? "";
                if (status is "completed" or "failed")
                    Append($"{(status == "completed" ? "  ✓" : "  ✗")} {_toolTitles.GetValueOrDefault(id, "tool")}\n", Kind.Tool);
                break;
            }
            case "plan":
                if (u["entries"] is JsonArray entries && entries.Count > 0)
                {
                    var lines = entries.Select(e => $"  {(e?["status"]?.ToString() == "completed" ? "☑" : e?["status"]?.ToString() == "in_progress" ? "▶" : "☐")} {e?["content"]}");
                    Append("Plan:\n" + string.Join("\n", lines) + "\n", Kind.Tool);
                }
                break;
        }
    }

    private static string? TextOf(JsonNode? content) =>
        content?["type"]?.ToString() == "text" ? content["text"]?.ToString() : null;

    private async Task<JsonNode?> HandleRequestAsync(string method, JsonObject? p)
    {
        switch (method)
        {
            case "session/request_permission":
            {
                if (AutoPermission(p) is { } auto)
                {
                    string title = p?["toolCall"]?["title"]?.ToString() ?? "tool";
                    Ui(() => Append($"✓ auto-allowed: {title}\n", Kind.Tool));
                    return new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = auto } };
                }
                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                Ui(() => AskPermission(p, tcs));
                string? option = await tcs.Task;
                return new JsonObject
                {
                    ["outcome"] = option == null
                        ? new JsonObject { ["outcome"] = "cancelled" }
                        : new JsonObject { ["outcome"] = "selected", ["optionId"] = option }
                };
            }
            case "fs/read_text_file":
            {
                string path = AllowedPath(p?["path"]?.ToString());
                if (!File.Exists(path)) throw new AcpError(-32002, "File not found: " + path);
                string text = await File.ReadAllTextAsync(path);
                int line = p?["line"]?.GetValue<int>() ?? 0, limit = p?["limit"]?.GetValue<int>() ?? 0;
                if (line > 1 || limit > 0)
                {
                    var lines = text.Split('\n').Skip(Math.Max(0, line - 1));
                    if (limit > 0) lines = lines.Take(limit);
                    text = string.Join("\n", lines);
                }
                return new JsonObject { ["content"] = text };
            }
            case "fs/write_text_file":
            {
                string path = AllowedPath(p?["path"]?.ToString());
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, p?["content"]?.ToString() ?? "");
                Log?.Invoke("[agent] wrote " + path);
                return null;
            }
            default:
                throw new AcpError(-32601, $"Method not supported by {AppInfo.Title}: " + method);
        }
    }

    /// <summary>File access is limited to the session folder (the mod).</summary>
    private string AllowedPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) throw new AcpError(-32602, "Missing path.");
        string root = System.IO.Path.GetFullPath(WorkingDirectory?.Invoke() ?? Environment.CurrentDirectory).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        string full = System.IO.Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new AcpError(-32602, $"Access outside {root} is not allowed.");
        return full;
    }

    private void AskPermission(JsonObject? p, TaskCompletionSource<string?> tcs)
    {
        if (p?["options"] is not JsonArray options || options.Count == 0) { tcs.TrySetResult(null); return; }
        string title = p["toolCall"]?["title"]?.ToString() ?? "The agent wants to run a tool.";
        _openPermissions.Add(tcs);
        tcs.Task.ContinueWith(_ => Ui(() => _openPermissions.Remove(tcs)));

        using var dlg = new Form
        {
            Text = "Agent permission", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10)
        };
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        flow.Controls.Add(new Label { Text = title, AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 10) });
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        foreach (var o in options)
        {
            string id = o?["optionId"]?.ToString() ?? "";
            var b = new Button { Text = o?["name"]?.ToString() ?? id, AutoSize = true };
            b.Click += (_, _) => { tcs.TrySetResult(id); dlg.Close(); };
            buttons.Controls.Add(b);
        }
        flow.Controls.Add(buttons);
        dlg.Controls.Add(flow);
        tcs.Task.ContinueWith(_ => Ui(() => { if (!dlg.IsDisposed) dlg.Close(); }));
        Append($"? {title}\n", Kind.Tool);
        dlg.ShowDialog(FindForm());
        tcs.TrySetResult(null);
    }

    // ---- output ----

    private void Append(string text, Kind kind)
    {
        if (IsDisposed) return;
        if (kind != _last)
        {
            if (_out.TextLength > 0 && !_out.Text.EndsWith('\n')) Write("\n", _out.ForeColor, _out.Font);
            if (kind == Kind.User) Write("You: ", Color.SteelBlue, _bold);
            else if (kind == Kind.Agent) Write("Agent: ", Color.SeaGreen, _bold);
            _last = kind;
        }
        var (color, font) = kind switch
        {
            Kind.Thought => (Color.Gray, _italic),
            Kind.Tool => (Color.DarkOrange, _out.Font),
            Kind.Info => (Color.DimGray, _italic),
            _ => (_out.ForeColor, _out.Font)
        };
        Write(text, color, font);
    }

    private void Write(string text, Color color, Font font)
    {
        _out.SelectionStart = _out.TextLength;
        _out.SelectionLength = 0;
        _out.SelectionColor = color;
        _out.SelectionFont = font;
        _out.AppendText(text);
        _out.ScrollToCaret();
    }

    private void SetState(string s) => _state.Text = s;

    private void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }
}
