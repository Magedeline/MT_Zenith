namespace CELMapViewer;

/// <summary>Room ▸ Resize / enlarge: pick a size tier (normal … xxl) or exact size held at an anchor, plus extra growth per edge.</summary>
public sealed class ResizeRoomForm : Form
{
    private readonly Rectangle _bounds;
    private readonly ComboBox _tier = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox _shape = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly NumericUpDown _w = Num(8, 40000), _h = Num(8, 40000);
    private readonly NumericUpDown _l = Num(-40000, 40000), _t = Num(-40000, 40000), _r = Num(-40000, 40000), _b = Num(-40000, 40000);
    private readonly RadioButton[] _anchor = new RadioButton[9];
    private readonly ComboBox _fill = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly CheckBox _push = new() { Text = "Push rooms beyond growing edges", AutoSize = true };
    private readonly Label _preview = new() { AutoSize = true };
    private bool _syncing;

    public int EdgeLeft, EdgeTop, EdgeRight, EdgeBottom;
    public RoomSizing.Fill FillMode => (RoomSizing.Fill)_fill.SelectedIndex;
    public bool PushRooms => _push.Checked;

    public ResizeRoomForm(Room room)
    {
        _bounds = room.Bounds;
        Text = $"Resize room {room.Name}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        _tier.Items.Add("(custom)");
        foreach (var t in RoomSizing.Tiers) _tier.Items.Add($"{t.Name} ({t.Screens} screen{(t.Screens > 1 ? "s" : "")})");
        _shape.Items.AddRange(RoomSizing.Shapes);
        _shape.SelectedIndex = 0;
        _fill.Items.AddRange(new object[] { "Extend edge tiles (keep walls closed)", "Leave empty", "Solid (tile 1)" });
        _fill.SelectedIndex = 0;
        _w.Value = _bounds.Width; _h.Value = _bounds.Height;
        _tier.SelectedIndex = 0;

        var grid = new TableLayoutPanel { ColumnCount = 3, RowCount = 3, AutoSize = true };
        for (int i = 0; i < 9; i++)
        {
            _anchor[i] = new RadioButton { AutoSize = true, Text = "", Tag = RoomSizing.Anchors[i], Margin = new Padding(6, 2, 6, 2) };
            new ToolTip().SetToolTip(_anchor[i], $"Keep {RoomSizing.Anchors[i]} fixed");
            _anchor[i].CheckedChanged += (_, _) => UpdatePreview();
            grid.Controls.Add(_anchor[i], i % 3, i / 3);
        }
        _anchor[0].Checked = true;

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c) { layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }); layout.Controls.Add(c); }
        Row("Current:", new Label { Text = $"{_bounds.Width}x{_bounds.Height} at ({_bounds.X},{_bounds.Y}) [{RoomSizing.TierOf(_bounds.Width, _bounds.Height)}]", AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
        Row("Size tier:", Flow(_tier, _shape));
        Row("Size (px):", Flow(_w, new Label { Text = "x", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, _h));
        Row("Anchor:", grid);
        Row("Extra (px):", Flow(Lbl("left"), _l, Lbl("top"), _t, Lbl("right"), _r, Lbl("bottom"), _b));
        var quick = Flow();
        foreach (var (txt, dir) in new[] { ("+1 screen ←", "l"), ("→", "r"), ("↑", "t"), ("↓", "b"), ("all", "lrtb"), ("Reset", "") })
        {
            var btn = new Button { Text = txt, AutoSize = true };
            btn.Click += (_, _) =>
            {
                if (dir.Length == 0) { _l.Value = _t.Value = _r.Value = _b.Value = 0; return; }
                if (dir.Contains('l')) _l.Value += RoomSizing.ScreenW;
                if (dir.Contains('r')) _r.Value += RoomSizing.ScreenW;
                if (dir.Contains('t')) _t.Value += RoomSizing.ScreenH;
                if (dir.Contains('b')) _b.Value += RoomSizing.ScreenH;
            };
            quick.Controls.Add(btn);
        }
        Row("", quick);
        Row("New space:", _fill);
        Row("", _push);
        Row("Result:", _preview);
        var ok = new Button { Text = "Resize", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        Row("", Flow(ok, cancel));
        AcceptButton = ok; CancelButton = cancel;
        Controls.Add(layout);

        _tier.SelectedIndexChanged += (_, _) => ApplyTier();
        _shape.SelectedIndexChanged += (_, _) => ApplyTier();
        foreach (var n in new[] { _w, _h }) n.ValueChanged += (_, _) => { if (!_syncing) _tier.SelectedIndex = 0; UpdatePreview(); };
        foreach (var n in new[] { _l, _t, _r, _b }) n.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        ok.Click += (_, _) => { if (!Compute(out _)) DialogResult = DialogResult.None; };
    }

    private static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Increment = 8, Width = 70 };
    private static Label Lbl(string s) => new() { Text = s, AutoSize = true, Margin = new Padding(4, 6, 0, 0) };
    private static FlowLayoutPanel Flow(params Control[] cs)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        f.Controls.AddRange(cs);
        return f;
    }

    private string AnchorName => (string)_anchor.First(a => a.Checked).Tag!;

    private void ApplyTier()
    {
        if (_tier.SelectedIndex <= 0) { UpdatePreview(); return; }
        var s = RoomSizing.TierSize(RoomSizing.Tiers[_tier.SelectedIndex - 1].Name, (string)_shape.SelectedItem!, _bounds.Size);
        _syncing = true;
        _w.Value = s.Width; _h.Value = s.Height;
        _syncing = false;
        UpdatePreview();
    }

    private bool Compute(out Rectangle nb)
    {
        var (l, t, r, b) = RoomSizing.DeltasFor(_bounds, new Size((int)_w.Value, (int)_h.Value), AnchorName);
        EdgeLeft = l + MapModel.Snap8((int)_l.Value); EdgeTop = t + MapModel.Snap8((int)_t.Value);
        EdgeRight = r + MapModel.Snap8((int)_r.Value); EdgeBottom = b + MapModel.Snap8((int)_b.Value);
        nb = Rectangle.FromLTRB(_bounds.Left - EdgeLeft, _bounds.Top - EdgeTop, _bounds.Right + EdgeRight, _bounds.Bottom + EdgeBottom);
        return nb.Width >= 8 && nb.Height >= 8;
    }

    private void UpdatePreview()
    {
        bool ok = Compute(out var nb);
        _preview.Text = ok
            ? $"{nb.Width}x{nb.Height} at ({nb.X},{nb.Y}) [{RoomSizing.TierOf(nb.Width, nb.Height)}]  edges L{EdgeLeft:+0;-0;0} T{EdgeTop:+0;-0;0} R{EdgeRight:+0;-0;0} B{EdgeBottom:+0;-0;0}"
            : "Too small (min 8x8)";
        _preview.ForeColor = ok ? Themes.Current.Text : Themes.Current.Error;
    }
}
