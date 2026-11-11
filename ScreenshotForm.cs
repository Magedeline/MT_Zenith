namespace CELMapViewer;

/// <summary>File ▸ Screenshot: pick the scale and what to draw, then save a PNG or copy it to the clipboard.</summary>
public sealed class ScreenshotForm : Form
{
    private static readonly float[] Scales = { 0.125f, 0.25f, 0.5f, 1f, 2f, 3f, 4f };

    private readonly RectangleF _area;
    private readonly ComboBox _scale = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly CheckBox _entities = new() { Text = "Entities", AutoSize = true };
    private readonly CheckBox _triggers = new() { Text = "Triggers", AutoSize = true };
    private readonly CheckBox _borders = new() { Text = "Room borders", AutoSize = true };
    private readonly CheckBox _labels = new() { Text = "Room names", AutoSize = true };
    private readonly CheckBox _transparent = new() { Text = "Transparent background", AutoSize = true };
    private readonly Label _size = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly Button _save = new() { Text = "Save PNG…", DialogResult = DialogResult.OK, AutoSize = true };
    private readonly Button _copy = new() { Text = "Copy to clipboard", DialogResult = DialogResult.Yes, AutoSize = true };

    /// <summary>True when the user picked "Copy to clipboard" rather than "Save PNG…".</summary>
    public bool ToClipboard => DialogResult == DialogResult.Yes;

    public ScreenshotOptions Options => new(Scales[_scale.SelectedIndex], _entities.Checked, _triggers.Checked,
                                             _borders.Checked, _labels.Checked, _transparent.Checked);

    public ScreenshotForm(string what, RectangleF area, ScreenshotOptions defaults)
    {
        _area = area;
        Text = $"Screenshot {what}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        foreach (var s in Scales) _scale.Items.Add(s >= 1 ? $"{s:0}x" : $"{s * 100:0.#}%");
        int best = Array.IndexOf(Scales, defaults.Scale);
        if (best < 0 || !MapCanvas.ShotFits(area, Scales[best])) best = Array.FindLastIndex(Scales, s => s <= defaults.Scale && MapCanvas.ShotFits(area, s));
        _scale.SelectedIndex = Math.Max(0, best);
        _entities.Checked = defaults.Entities;
        _triggers.Checked = defaults.Triggers;
        _borders.Checked = defaults.Borders;
        _labels.Checked = defaults.Labels;
        _transparent.Checked = defaults.Transparent;

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c) { layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }); layout.Controls.Add(c); }
        Row("Area:", new Label { Text = $"{area.Width:0}x{area.Height:0} px at ({area.X:0},{area.Y:0})", AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
        Row("Scale:", _scale);
        Row("Show:", Flow(_entities, _triggers, _borders, _labels));
        Row("", _transparent);
        Row("Output:", _size);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        Row("", Flow(_save, _copy, cancel));
        AcceptButton = _save; CancelButton = cancel;
        Controls.Add(layout);

        _scale.SelectedIndexChanged += (_, _) => UpdateSize();
        UpdateSize();
    }

    private static FlowLayoutPanel Flow(params Control[] cs)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        f.Controls.AddRange(cs);
        return f;
    }

    private void UpdateSize()
    {
        float s = Scales[_scale.SelectedIndex];
        var sz = MapCanvas.ShotSize(_area, s);
        bool ok = MapCanvas.ShotFits(_area, s);
        _size.Text = ok ? $"{sz.Width}x{sz.Height} px" : $"{sz.Width}x{sz.Height} px — too large, pick a smaller scale";
        _size.ForeColor = ok ? Themes.Current.Text : Themes.Current.Error;
        _save.Enabled = _copy.Enabled = ok;
    }
}
