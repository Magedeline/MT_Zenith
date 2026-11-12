using System.Globalization;

namespace CELMapViewer;

/// <summary>
/// Lönn-style room dialog used for both "Creating room" and "Room properties": name, position, size (in tiles),
/// colour, camera offset, wind, flags, music layers, music and ambience.
/// </summary>
public sealed class RoomPropertiesForm : Form
{
    private static readonly string[] MusicTracks =
    {
        "", "music_city", "music_oldsite_awake", "music_oldsite_beginning", "music_oldsite_chase", "music_resort_intro",
        "music_resort_explore", "music_resort_oshiro_theme", "music_cliffside_main", "music_cliffside_panic",
        "music_reflection_main", "music_reflection_maddietheo", "music_reflection_fight", "music_reflection_fight_finish",
        "music_summit_main", "music_core_main", "music_lvl9_main", "music_farewell_part01", "music_farewell_part02",
        "music_farewell_part03", "music_farewell_final_run", "music_farewell_end_cinematic", "music_prologue_beginning",
        "music_prologue_collapse", "music_prologue_escape", "music_epilogue_main", "music_credits", "music_pico8_area1"
    };
    private static readonly string[] Ambiences =
    {
        "", "env_amb_00_main", "env_amb_01_main", "env_amb_02_awake", "env_amb_02_dream", "env_amb_03_exterior",
        "env_amb_03_interior", "env_amb_03_pico8_closeup", "env_amb_04_main", "env_amb_05_interior_dark",
        "env_amb_05_interior_main", "env_amb_05_mirror_sequence", "env_amb_06_lake", "env_amb_06_main",
        "env_amb_06_prehug", "env_amb_09_main", "env_amb_10_rain", "env_amb_10_grannyclouds", "env_amb_10_voidspiral",
        "env_amb_worldmap"
    };

    private readonly TextBox _name = new();
    private readonly NumericUpDown _x = Num(-1_000_000, 1_000_000), _y = Num(-1_000_000, 1_000_000);
    private readonly NumericUpDown _w = Num(1, 10_000), _h = Num(1, 10_000);
    private readonly ComboBox _colour = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _customColour = new() { PlaceholderText = "hex, e.g. ff8040" };
    private readonly NumericUpDown _camX = Num(-10_000, 10_000, 1), _camY = Num(-10_000, 10_000, 1);
    private readonly ComboBox _wind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _underwater = Chk("Underwater"), _space = Chk("Space"), _disableDown = Chk("Disable Down Transition"),
        _checkpoint = Chk("Checkpoint"), _dark = Chk("Dark"), _whisper = Chk("Whisper");
    private readonly CheckBox[] _layers = Enumerable.Range(1, 4).Select(i => Chk("Music Layer " + i)).ToArray();
    private readonly ComboBox _music = new() { DropDownStyle = ComboBoxStyle.DropDown }, _altMusic = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _ambience = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _musicProgress = new(), _ambienceProgress = new();
    private readonly Func<string, bool> _nameTaken;

    public string RoomName => _name.Text.Trim();
    public Rectangle Bounds8 => new((int)_x.Value, (int)_y.Value, (int)_w.Value * 8, (int)_h.Value * 8);

    /// <param name="level">The level to show (its values fill the form). For a new room pass a fresh <see cref="MapModel.NewLevel"/>.</param>
    /// <param name="nameTaken">True when another room already uses a name.</param>
    public RoomPropertiesForm(BinElement level, bool creating, Func<string, bool> nameTaken)
    {
        _nameTaken = nameTaken;
        Text = creating ? "Creating room" : "Room properties";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        _colour.Items.AddRange(Enumerable.Range(0, 7).Select(i => (object)i.ToString(CultureInfo.InvariantCulture)).ToArray());
        _wind.Items.AddRange(PluginCatalog.WindPatterns);
        _music.Items.AddRange(MusicTracks);
        _altMusic.Items.AddRange(MusicTracks);
        _ambience.Items.AddRange(Ambiences);

        var grid = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill };
        for (int i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, i % 2 == 0 ? 160 : 180));
        void Row(string l1, Control c1, string? l2 = null, Control? c2 = null)
        {
            grid.Controls.Add(Lbl(l1)); grid.Controls.Add(Fit(c1));
            if (l2 != null && c2 != null) { grid.Controls.Add(Lbl(l2)); grid.Controls.Add(Fit(c2)); }
            else { grid.Controls.Add(new Label()); grid.Controls.Add(new Label()); }
        }
        void Checks(params CheckBox[] boxes) { foreach (var b in boxes) grid.Controls.Add(Fit(b)); }

        Row("Room Name", _name);
        Row("X", _x, "Y", _y);
        Row("Width (tiles)", _w, "Height (tiles)", _h);
        Row("Colour", _colour, "Custom Colour", _customColour);
        Row("Camera Offset X", _camX, "Camera Offset Y", _camY);
        grid.Controls.Add(Lbl("Wind Pattern")); grid.Controls.Add(Fit(_wind)); Checks(_underwater, _space);
        Checks(_disableDown, _checkpoint, _dark, _whisper);
        Checks(_layers);
        Row("Music", _music, "Music Progress", _musicProgress);
        Row("Alt Music", _altMusic);
        Row("Ambience", _ambience, "Ambience Progress", _ambienceProgress);

        var ok = new Button { Text = creating ? "Create room" : "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => { if (Validate(out string? err)) { DialogResult = DialogResult.OK; Close(); } else MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
        _name.TextChanged += (_, _) => _name.BackColor = RoomName.Length == 0 || _nameTaken(RoomName) ? Color.FromArgb(255, 210, 210) : SystemColors.Window;

        LoadFrom(level);
    }

    private bool Validate(out string? error)
    {
        error = null;
        if (RoomName.Length == 0) error = "The room needs a name.";
        else if (_nameTaken(RoomName)) error = $"A room named '{RoomName}' already exists.";
        else if (_customColour.Text.Trim().Length > 0 && PluginCatalog.ParseHex(_customColour.Text.Trim()) == null) error = "Custom colour must be a hex colour such as ff8040.";
        return error == null;
    }

    private void LoadFrom(BinElement l)
    {
        string name = l.Str("name");
        _name.Text = name.StartsWith("lvl_") ? name[4..] : name;
        _x.Value = Clamp(_x, l.Int("x")); _y.Value = Clamp(_y, l.Int("y"));
        _w.Value = Clamp(_w, Math.Max(1, (l.Int("width") + 7) / 8)); _h.Value = Clamp(_h, Math.Max(1, (l.Int("height") + 7) / 8));
        _colour.SelectedIndex = Math.Clamp(l.Int("c"), 0, 6);
        _customColour.Text = l.Str("color");
        _camX.Value = (decimal)Math.Clamp(l.Num("cameraOffsetX"), -10_000, 10_000);
        _camY.Value = (decimal)Math.Clamp(l.Num("cameraOffsetY"), -10_000, 10_000);
        string wind = l.Str("windPattern", "None");
        _wind.SelectedItem = PluginCatalog.WindPatterns.Contains(wind) ? wind : "None";
        _underwater.Checked = Bool(l, "underwater"); _space.Checked = Bool(l, "space");
        _disableDown.Checked = Bool(l, "disableDownTransition"); _dark.Checked = Bool(l, "dark"); _whisper.Checked = Bool(l, "whisper");
        _checkpoint.Checked = l.Child("entities")?.Children.Any(e => e.Name == "checkpoint") == true || Bool(l, "checkpoint");
        for (int i = 0; i < 4; i++) _layers[i].Checked = Bool(l, "musicLayer" + (i + 1), true);
        _music.Text = l.Str("music"); _altMusic.Text = l.Str("alt_music"); _ambience.Text = l.Str("ambience");
        _musicProgress.Text = l.Str("musicProgress"); _ambienceProgress.Text = l.Str("ambienceProgress");
    }

    /// <summary>Writes every field except name and bounds (the caller applies those so tiles move with a resize).</summary>
    public void ApplyTo(BinElement l)
    {
        l.Set("c", _colour.SelectedIndex < 0 ? 0 : _colour.SelectedIndex);
        string custom = _customColour.Text.Trim().TrimStart('#');
        if (custom.Length > 0) l.Set("color", custom); else l.Remove("color");
        l.Set("cameraOffsetX", (float)_camX.Value);
        l.Set("cameraOffsetY", (float)_camY.Value);
        l.Set("windPattern", _wind.SelectedItem as string ?? "None");
        l.Set("underwater", _underwater.Checked); l.Set("space", _space.Checked);
        l.Set("disableDownTransition", _disableDown.Checked); l.Set("dark", _dark.Checked); l.Set("whisper", _whisper.Checked);
        for (int i = 0; i < 4; i++) l.Set("musicLayer" + (i + 1), _layers[i].Checked);
        l.Set("music", _music.Text.Trim()); l.Set("alt_music", _altMusic.Text.Trim()); l.Set("ambience", _ambience.Text.Trim());
        l.Set("musicProgress", _musicProgress.Text.Trim()); l.Set("ambienceProgress", _ambienceProgress.Text.Trim());
    }

    /// <summary>True when the "Checkpoint" box asks for a checkpoint entity.</summary>
    public bool WantsCheckpoint => _checkpoint.Checked;

    private static bool Bool(BinElement l, string key, bool def = false) =>
        l.Find(key)?.Value is bool b ? b : l.Find(key)?.Value is string s ? s.Equals("true", StringComparison.OrdinalIgnoreCase) : def;
    private static decimal Clamp(NumericUpDown n, int v) => Math.Clamp(v, n.Minimum, n.Maximum);
    private static NumericUpDown Num(int min, int max, int decimals = 0) => new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals > 0 ? 0.5m : 1 };
    private static CheckBox Chk(string text) => new() { Text = text, AutoSize = true };
    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
    private static Control Fit(Control c) { if (c is not CheckBox) c.Width = 170; c.Margin = new Padding(3); return c; }
}
