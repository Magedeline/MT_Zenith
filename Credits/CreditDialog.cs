namespace CELMapViewer;

/// <summary>Asks who made an asset: the mapper ("my own work") or someone else (author, where it's from, link).</summary>
public sealed class CreditDialog : Form
{
    private readonly RadioButton _mine = new() { Text = "I made it (my own work)", AutoSize = true, Checked = true };
    private readonly RadioButton _other = new() { Text = "Someone else made it:", AutoSize = true };
    private readonly TextBox _author = new() { Width = 320, PlaceholderText = "Author (who to credit)" };
    private readonly TextBox _source = new() { Width = 320, PlaceholderText = "Where it's from (mod, Asset Drive, website…)" };
    private readonly TextBox _url = new() { Width = 320, PlaceholderText = "Link (GameBanana page, maddie480.ovh, …)" };
    private readonly TextBox _notes = new() { Width = 320, Height = 60, Multiline = true, PlaceholderText = "Their terms / notes (optional)" };
    private readonly CheckBox _folder = new() { Text = "Apply to the whole folder", AutoSize = true };
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };

    public static readonly (string Label, string Category)[] Kinds =
    {
        ("Decal", "decals"), ("Styleground", "stylegrounds"), ("Foreground tileset", "fgtilesets"),
        ("Background tileset", "bgtilesets"), ("Other sprite", "misc"),
    };

    /// <param name="what">What is being credited (shown at the top).</param>
    /// <param name="askKind">Show the "Import as" kind picker (for importing files).</param>
    /// <param name="folderOption">Show "Apply to the whole folder".</param>
    public CreditDialog(string what, bool askKind, bool folderOption, RecordedCredit? existing = null)
    {
        Text = "Who made this?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        t.Controls.Add(new Label { Text = what, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 8) });
        if (askKind)
        {
            foreach (var k in Kinds) _kind.Items.Add(k.Label);
            _kind.SelectedIndex = 0;
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            row.Controls.Add(new Label { Text = "Import as:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            row.Controls.Add(_kind);
            t.Controls.Add(row);
        }
        t.Controls.Add(_mine);
        t.Controls.Add(_other);
        foreach (var c in new Control[] { _author, _source, _url, _notes }) { c.Margin = new Padding(20, 2, 0, 2); t.Controls.Add(c); }
        if (folderOption) t.Controls.Add(_folder);
        t.Controls.Add(new Label
        {
            Text = "Using someone else's asset? Check their terms (most Asset Drive and GameBanana creators ask for credit) " +
                   "and keep this record so your credits.txt names them.",
            AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Themes.Current.Muted, Margin = new Padding(0, 8, 0, 8),
        });
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        t.Controls.Add(buttons);
        Controls.Add(t);
        AcceptButton = ok; CancelButton = cancel;

        if (existing != null)
        {
            _other.Checked = !existing.Own;
            _author.Text = existing.Author; _source.Text = existing.Source; _url.Text = existing.Url; _notes.Text = existing.Notes;
        }
        void Sync() { foreach (var c in new Control[] { _author, _source, _url, _notes }) c.Enabled = _other.Checked; }
        _other.CheckedChanged += (_, _) => Sync();
        Sync();
        ok.Click += (_, _) =>
        {
            if (_other.Checked && _author.Text.Trim().Length == 0)
            {
                MessageBox.Show(this, "Enter who made it, so they can be credited.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
            }
        };
    }

    public string Category => _kind.SelectedIndex >= 0 ? Kinds[_kind.SelectedIndex].Category : "decals";
    public bool WholeFolder => _folder.Checked;

    public RecordedCredit Credit => _mine.Checked
        ? new RecordedCredit { Own = true, Author = "", Source = "Own work" }
        : new RecordedCredit
        {
            Own = false, Author = _author.Text.Trim(), Source = _source.Text.Trim(), Url = _url.Text.Trim(),
            Notes = _notes.Text.Trim(),
        };
}
