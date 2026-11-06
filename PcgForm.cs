namespace CELMapViewer;

/// <summary>Front-end for PCGHelper's seleCte generator. On OK, <see cref="Result"/> holds the rooms to import.</summary>
public sealed class PcgForm : Form
{
    public PcgResult? Result { get; private set; }
    public string Prefix => _prefix.Text.Trim();

    private readonly PcgGenerator _gen;
    private readonly TextBox _seed = new() { Width = 160 };
    private readonly NumericUpDown _rooms = Num(1, 60, 7);
    private readonly NumericUpDown _w = Num(10, 200, 40);
    private readonly NumericUpDown _h = Num(10, 200, 23);
    private readonly NumericUpDown _proba = new() { Minimum = 0, Maximum = 1, DecimalPlaces = 2, Increment = 0.05m, Value = 0.3m, Width = 70 };
    private readonly NumericUpDown _tries = Num(1, 100, 10);
    private readonly NumericUpDown _bt = Num(0, 10, 0);
    private readonly CheckBox _reset = new() { Text = "Reset skeleton on failure", AutoSize = true };
    private readonly ComboBox _model = Combo(PcgGenerator.Models);
    private readonly ComboBox _bg = Combo(PcgGenerator.Backgrounds);
    private readonly ComboBox _solid = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox _prefix = new() { Text = "pcg_", Width = 80 };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    private readonly Button _go = new() { Text = "Generate", Width = 90 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 90, Enabled = false };
    private readonly Button _import = new() { Text = "Import into map", Width = 120, Enabled = false };
    private readonly Label _status = new() { AutoSize = true };

    private static NumericUpDown Num(int min, int max, int v) => new() { Minimum = min, Maximum = max, Value = v, Width = 70 };
    private static ComboBox Combo(string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        c.Items.AddRange(items);
        c.SelectedIndex = 0;
        return c;
    }

    public PcgForm(PcgGenerator gen)
    {
        _gen = gen;
        Text = "Generate rooms — PCGHelper seleCte";
        Size = new Size(720, 600);
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
        Row("Room name prefix", _prefix, "", _reset);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 0, 8, 4) };
        buttons.Controls.AddRange(new Control[] { _go, _cancel, _import, _status });

        Controls.Add(_log);
        Controls.Add(buttons);
        Controls.Add(grid);

        try
        {
            foreach (var (glyph, name) in gen.SolidTilesets()) _solid.Items.Add($"{glyph}  {name}");
        }
        catch (Exception ex) { Log("Could not read alphabet.lua: " + ex.Message); }
        if (_solid.Items.Count == 0) _solid.Items.Add("1  dirt");
        _solid.SelectedIndex = 0;

        _go.Click += async (_, _) => await GenerateAsync();
        _cancel.Click += (_, _) => _gen.Cancel();
        _import.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        FormClosing += (_, e) => { if (_cancel.Enabled) { _gen.Cancel(); e.Cancel = true; } };
        Log("Library: " + gen.LoennRoot);
    }

    private void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
        _log.AppendText(s.Replace("\n", Environment.NewLine) + Environment.NewLine);
    }

    private async Task GenerateAsync()
    {
        var o = new PcgOptions
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
        };
        _go.Enabled = _import.Enabled = false;
        _cancel.Enabled = true;
        _status.Text = "Generating…";
        Result = null;
        try
        {
            var r = await Task.Run(() => _gen.Generate(o, Log));
            Result = r;
            Log($"Done: {r.Rooms.Count} rooms, seed \"{r.Seed}\", {(r.AllPlayable ? "all playable" : "NOT all rooms playable")}, {r.Elapsed.TotalSeconds:0.0}s");
            if (r.Route.Count > 0) Log("Route: " + string.Join(" → ", r.Route));
            _seed.Text = r.Seed;
            _status.Text = $"{r.Rooms.Count} rooms ready";
            _import.Enabled = r.Rooms.Count > 0;
        }
        catch (Exception ex)
        {
            string msg = ex is MoonSharp.Interpreter.InterpreterException ie ? ie.DecoratedMessage ?? ie.Message : ex.Message;
            Log(msg.Contains("cancelled") ? "Cancelled." : "Failed: " + msg);
            _status.Text = "";
        }
        finally
        {
            _go.Enabled = true;
            _cancel.Enabled = false;
        }
    }
}
