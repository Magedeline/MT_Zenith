using CELMapViewer.Agent;

namespace CELMapViewer;

/// <summary>
/// Front-end for the PCG pipeline (PCGHelper's seleCte sampler + candidate search, repair, tier shaping, movement-model
/// judgement, preview and model training). On OK, <see cref="Result"/> holds the rooms to import.
/// </summary>
public sealed class PcgForm : Form
{
    public PcgCandidate? Candidate { get; private set; }
    public PcgResult? Result => Candidate?.Result;
    public string Prefix => _prefix.Text.Trim();

    private readonly PcgGenerator _gen;
    private readonly Func<MapBin?> _currentMap;
    private readonly Func<(Autotiler? Fg, Autotiler? Bg)> _tilers;
    private readonly TextBox _seed = new() { Width = 160 };
    private readonly NumericUpDown _rooms = Num(1, 60, 7);
    private readonly NumericUpDown _w = Num(8, 200, 40);
    private readonly NumericUpDown _h = Num(8, 200, 23);
    private readonly NumericUpDown _proba = new() { Minimum = 0, Maximum = 1, DecimalPlaces = 2, Increment = 0.05m, Value = 0.3m, Width = 70 };
    private readonly NumericUpDown _tries = Num(1, 100, 10);
    private readonly NumericUpDown _bt = Num(0, 10, 0);
    private readonly NumericUpDown _candidates = Num(1, 24, 3);
    private readonly CheckBox _reset = new() { Text = "Reset skeleton on failure", AutoSize = true };
    private readonly CheckBox _repair = new() { Text = "Repair artefacts (floating spikes, buried spinners, spawn ground, exit mouths)", AutoSize = true, Checked = true };
    private readonly ComboBox _model = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _bg = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _solid = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _tier = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckedListBox _mechanics = new() { CheckOnClick = true, MultiColumn = true, ColumnWidth = 118, Height = 56, Width = 600, BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _prefix = new() { Text = "pcg_", Width = 80 };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    private readonly Button _go = new() { Text = "Generate", Width = 90 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 90, Enabled = false };
    private readonly Button _preview = new() { Text = "Preview…", Width = 90, Enabled = false };
    private readonly Button _train = new() { Text = "Train model…", Width = 100 };
    private readonly Button _import = new() { Text = "Import into map", Width = 120, Enabled = false };
    private readonly Label _status = new() { AutoSize = true };
    private CancellationTokenSource? _cts;

    private static NumericUpDown Num(int min, int max, int v) => new() { Minimum = min, Maximum = max, Value = v, Width = 70 };

    public PcgForm(PcgGenerator gen, Func<MapBin?> currentMap, Func<(Autotiler? Fg, Autotiler? Bg)> tilers)
    {
        _gen = gen;
        _currentMap = currentMap;
        _tilers = tilers;
        Text = "Generate rooms — PCG (seleCte Markov sampler + repair, tier shaping, movement check)";
        Size = new Size(820, 720);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(8) };
        void Row(string a, Control ca, string b, Control cb)
        {
            grid.Controls.Add(new Label { Text = a, AutoSize = true, Anchor = AnchorStyles.Left });
            grid.Controls.Add(ca);
            grid.Controls.Add(new Label { Text = b, AutoSize = true, Anchor = AnchorStyles.Left });
            grid.Controls.Add(cb);
        }
        Row("Seed (blank = random)", _seed, "Rooms", _rooms);
        Row("Room width (tiles)", _w, "Room height (tiles)", _h);
        Row("Model", _model, "Branch probability", _proba);
        Row("Background model", _bg, "Tries per room", _tries);
        Row("Solid tileset", _solid, "Backtrack depth", _bt);
        Row("Target tier", _tier, "Candidates (best of)", _candidates);
        Row("Room name prefix", _prefix, "", _reset);
        grid.Controls.Add(new Label { Text = "Mechanics allowed", AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(_mechanics);
        grid.SetColumnSpan(_mechanics, 3);
        grid.Controls.Add(new Label { Text = "", AutoSize = true });
        grid.Controls.Add(_repair);
        grid.SetColumnSpan(_repair, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 0, 8, 4) };
        buttons.Controls.AddRange(new Control[] { _go, _cancel, _preview, _train, _import, _status });

        Controls.Add(_log);
        Controls.Add(buttons);
        Controls.Add(grid);

        _tier.Items.Add("any");
        for (int t = 1; t <= 5; t++) _tier.Items.Add(ReferenceMaps.TierNames[t]);
        _tier.SelectedIndex = 0;
        foreach (var (label, _) in PcgPipeline.Mechanics) _mechanics.Items.Add(label, true);
        RefreshModels();
        try
        {
            foreach (var (glyph, name) in gen.SolidTilesets()) _solid.Items.Add($"{glyph}  {name}");
        }
        catch (Exception ex) { Log("Could not read alphabet.lua: " + ex.Message); }
        if (_solid.Items.Count == 0) _solid.Items.Add("1  dirt");
        _solid.SelectedIndex = 0;

        _go.Click += async (_, _) => await GenerateAsync();
        _cancel.Click += (_, _) => { _cts?.Cancel(); _gen.Cancel(); };
        _preview.Click += (_, _) => ShowPreview();
        _train.Click += async (_, _) => await TrainAsync();
        _import.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        FormClosing += (_, e) => { if (_cancel.Enabled) { _cts?.Cancel(); _gen.Cancel(); e.Cancel = true; } };
        Log("Library: " + gen.LoennRoot);
        Log("Candidates > 1 generates seed, seed#1, seed#2 … and keeps the best by traversability, hazard pressure (vs tier) and variety. " +
            "Every room is judged with the movement model (jump, one dash, climb); see the per-room lines after generation.");
    }

    private void RefreshModels()
    {
        string? m = _model.SelectedItem as string, b = _bg.SelectedItem as string;
        _model.Items.Clear(); _bg.Items.Clear();
        foreach (string s in PcgGenerator.ModelNames()) _model.Items.Add(s);
        foreach (string s in PcgGenerator.BackgroundNames()) _bg.Items.Add(s);
        _model.SelectedIndex = Math.Max(0, _model.Items.IndexOf(m ?? ""));
        _bg.SelectedIndex = Math.Max(0, _bg.Items.IndexOf(b ?? ""));
    }

    private void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
        _log.AppendText(s.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine);
    }

    private void Busy(bool busy, string status)
    {
        _go.Enabled = _train.Enabled = !busy;
        _import.Enabled = !busy && Candidate is { Result.Rooms.Count: > 0 };
        _preview.Enabled = _import.Enabled;
        _cancel.Enabled = busy;
        _status.Text = status;
    }

    private async Task GenerateAsync()
    {
        var allowed = new List<string>();
        foreach (int i in _mechanics.CheckedIndices) allowed.Add(PcgPipeline.Mechanics[i].Label);
        var o = new PcgPipelineOptions
        {
            Base = new PcgOptions
            {
                Seed = _seed.Text.Trim(),
                NbRooms = (int)_rooms.Value,
                RoomW = (int)_w.Value,
                RoomH = (int)_h.Value,
                Proba = (double)_proba.Value,
                TriesLimit = (int)_tries.Value,
                BtDepth = (int)_bt.Value,
                ResetSkeleton = _reset.Checked,
                Model = (string)_model.SelectedItem!,
                Background = (string)_bg.SelectedItem!,
                Solid = ((string)_solid.SelectedItem!).Split(' ')[0],
            },
            Candidates = (int)_candidates.Value,
            Repair = _repair.Checked,
            TargetTier = _tier.SelectedIndex > 0 ? _tier.SelectedIndex : null,
            Allowed = allowed.Count == PcgPipeline.Mechanics.Length ? null : PcgPipeline.ParseAllowed(allowed) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Analyze = true,
        };
        Candidate = null;
        Busy(true, "Generating…");
        _cts = new CancellationTokenSource();
        try
        {
            var c = await Task.Run(() => PcgPipeline.Run(_gen, o, Log, _cts.Token));
            Candidate = c;
            var r = c.Result;
            Log($"Done: {r.Rooms.Count} rooms, seed \"{r.Seed}\", {(r.AllPlayable ? "seleCte route playable" : "seleCte route NOT playable")}, score {c.Score:0.0}, {r.Elapsed.TotalSeconds:0.0}s per level");
            if (r.Route.Count > 0) Log("Route: " + string.Join(" → ", r.Route));
            if (c.Fixes.Count > 0) Log("Repairs: " + string.Join("; ", c.Fixes.Take(20)) + (c.Fixes.Count > 20 ? $" … (+{c.Fixes.Count - 20})" : ""));
            if (c.Report["room_details"] is System.Text.Json.Nodes.JsonArray details)
            {
                Log($"Movement model: {c.Report["rooms_fully_traversable"]} rooms fully traversable, exits {c.Report["exits_reachable"]}, hazard pressure {c.Report["mean_hazard_pressure"]} (feels {c.Report["tier_feel"]}).");
                foreach (var d in details)
                    Log($"  {d?["room"]}: exits {d?["exits_reached"]}/{d?["exits"]}, tech {d?["required_tech"]}, pressure {d?["hazard_pressure"]}, {d?["entities"]} entities");
            }
            _seed.Text = r.Seed;
            Busy(false, $"{r.Rooms.Count} rooms ready");
        }
        catch (Exception ex)
        {
            string msg = ex is MoonSharp.Interpreter.InterpreterException ie ? ie.DecoratedMessage ?? ie.Message : ex.Message;
            Log(ex is OperationCanceledException || msg.Contains("cancelled") ? "Cancelled." : "Failed: " + msg);
            Busy(false, "");
        }
    }

    private void ShowPreview()
    {
        if (Candidate == null) return;
        try
        {
            var (fg, bg) = _tilers();
            var bmp = PcgPipeline.Preview(Candidate.Result, fg, bg, 1800);
            var f = new Form { Text = $"Preview — seed {Candidate.Result.Seed}", Size = new Size(Math.Min(1400, bmp.Width + 40), Math.Min(900, bmp.Height + 60)), StartPosition = FormStartPosition.CenterParent };
            var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var pic = new PictureBox { Image = bmp, SizeMode = PictureBoxSizeMode.AutoSize };
            panel.Controls.Add(pic);
            f.Controls.Add(panel);
            f.FormClosed += (_, _) => bmp.Dispose();
            f.Show(this);
        }
        catch (Exception ex) { Log("Preview failed: " + ex.Message); }
    }

    // ---- training ----

    private async Task TrainAsync()
    {
        using var dlg = new TrainDialog(_currentMap() != null);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Busy(true, "Collecting rooms…");
        _cts = new CancellationTokenSource();
        try
        {
            var (name, layer, source, tier, filter, maxRooms) = (dlg.ModelName, dlg.Layer, dlg.Source, dlg.Tier, dlg.Filter, dlg.MaxRooms);
            var result = await Task.Run(() =>
            {
                var rooms = new List<CorpusRoom>();
                string sources;
                if (source == "open_map")
                {
                    var map = _currentMap() ?? throw new InvalidOperationException("No map is open.");
                    rooms.AddRange(PcgPipeline.Corpus(map));
                    sources = "open map";
                }
                else
                {
                    var maps = ReferenceMaps.All().Where(m => m.Kind == "map" && (tier == null || m.Tier == tier) && (filter.Length == 0 || m.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (maps.Count == 0) throw new InvalidOperationException("No reference maps match that tier/filter.");
                    Log($"Reading {maps.Count} reference maps…");
                    foreach (var m in maps)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        if (rooms.Count >= maxRooms) break;
                        try { rooms.AddRange(PcgPipeline.Corpus(ReferenceMaps.Load(m)).Take(maxRooms - rooms.Count)); }
                        catch (Exception) { }
                    }
                    sources = $"{maps.Count} reference maps" + (tier is int t ? $", tier {ReferenceMaps.TierNames[t]}" : "") + (filter.Length > 0 ? $", filter '{filter}'" : "");
                }
                Log($"Training {layer} model '{name}' on {rooms.Count} rooms ({sources})…");
                var tr = _gen.Train(rooms, layer, Log);
                var info = PcgGenerator.SaveModel(name, layer, tr, sources);
                return (info, tr);
            }, _cts.Token);
            Log($"Saved model '{result.info.Name}' ({result.tr.Patterns} patterns from {result.tr.RoomsKept}/{result.tr.RoomsTotal} rooms, {result.tr.Entities} entities) to {result.info.Path}");
            RefreshModels();
            if (layer_is_fg(result.info)) _model.SelectedItem = result.info.Name; else _bg.SelectedItem = result.info.Name;
            Busy(false, "Model ready");
        }
        catch (Exception ex)
        {
            string msg = ex is MoonSharp.Interpreter.InterpreterException ie ? ie.DecoratedMessage ?? ie.Message : ex.Message;
            Log(ex is OperationCanceledException ? "Cancelled." : "Training failed: " + msg);
            Busy(false, "");
        }

        static bool layer_is_fg(PcgModelInfo i) => i.Layer == "fg";
    }

    /// <summary>Name / layer / source for a new Markov model.</summary>
    private sealed class TrainDialog : Form
    {
        private readonly TextBox _name = new() { Width = 200, Text = "custom_fg" };
        private readonly ComboBox _layer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        private readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        private readonly ComboBox _tier = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        private readonly TextBox _filter = new() { Width = 200, PlaceholderText = "e.g. StrawberryJam2021" };
        private readonly NumericUpDown _max = new() { Minimum = 10, Maximum = 20000, Value = 1500, Width = 90 };

        public string ModelName => _name.Text.Trim();
        public string Layer => _layer.SelectedIndex == 1 ? "bg" : "fg";
        public string Source => _source.SelectedIndex == 0 ? "open_map" : "reference";
        public int? Tier => _tier.SelectedIndex > 0 ? _tier.SelectedIndex : null;
        public string Filter => _filter.Text.Trim();
        public int MaxRooms => (int)_max.Value;

        public TrainDialog(bool hasMap)
        {
            Text = "Train a Markov model";
            FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(10);
            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
            void Row(string label, Control c) { grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }); grid.Controls.Add(c); }
            _layer.Items.AddRange(new object[] { "fg (layout + entities)", "bg (background tiles)" }); _layer.SelectedIndex = 0;
            _source.Items.AddRange(new object[] { "open map", "installed reference maps" }); _source.SelectedIndex = hasMap ? 0 : 1;
            _tier.Items.Add("any tier");
            for (int t = 1; t <= 5; t++) _tier.Items.Add(ReferenceMaps.TierNames[t]);
            _tier.SelectedIndex = 0;
            Row("Model name", _name); Row("Layer", _layer); Row("Rooms from", _source); Row("Reference tier", _tier); Row("Reference filter", _filter); Row("Max rooms", _max);
            var ok = new Button { Text = "Train", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom };
            buttons.Controls.AddRange(new Control[] { ok, cancel });
            var note = new Label { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 8, 0, 8),
                Text = "Foreground models learn room layout plus spikes, spinners, boosters, refills, dream/crumble blocks, jump-throughs, water and feathers from the chosen rooms " +
                       "(custom tilesets count as solid). Pick a collab tier to get rooms shaped like that difficulty." };
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
            flow.Controls.AddRange(new Control[] { grid, note, buttons });
            Controls.Add(flow);
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += (_, e) => { if (ModelName.Length == 0) { DialogResult = DialogResult.None; MessageBox.Show(this, "Give the model a name."); } };
        }
    }
}
