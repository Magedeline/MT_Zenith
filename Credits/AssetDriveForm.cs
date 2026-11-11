using System.Diagnostics;

namespace CELMapViewer;

/// <summary>
/// Browser for the Celeste Community Asset Drive (via maddie480.ovh): find a community decal, styleground or tileset,
/// read its author's terms, and add it to your mod with the credit recorded automatically.
/// </summary>
public sealed class AssetDriveForm : Form
{
    private readonly string? _modDir;
    private readonly string _modName;
    private readonly Action<string> _imported;
    private readonly ComboBox _category = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly TextBox _search = new() { Width = 300, PlaceholderText = "Search name, author or tag…" };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Top, Height = 220, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(40, 40, 48) };
    private readonly TextBox _info = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true };
    private readonly Button _add = new() { Text = "Add to my mod (with credit)", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
    private List<DriveAsset> _all = new();
    private readonly Dictionary<DriveAsset, string> _why = new();
    private readonly Dictionary<string, double>? _keywords;
    private readonly ISet<string> _authors;
    private CancellationTokenSource? _pick;
    public const string RecommendedLabel = "★ Recommended for this map";

    /// <param name="keywords">Theme words of the open map (see <see cref="CreditAdvice.MapKeywords"/>); enables recommendations.</param>
    /// <param name="creditedAuthors">Creators the map already credits (recommended a little higher).</param>
    /// <param name="search">Initial search text.</param>
    public AssetDriveForm(string? modDir, string modName, Action<string> imported,
        Dictionary<string, double>? keywords = null, ISet<string>? creditedAuthors = null, string? search = null)
    {
        _modDir = modDir; _modName = modName; _imported = imported;
        _keywords = keywords is { Count: > 0 } ? keywords : null;
        _authors = creditedAuthors ?? new HashSet<string>();
        Text = "Celeste Community Asset Drive (maddie480.ovh)";
        Size = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;

        if (_keywords != null) _category.Items.Add(RecommendedLabel);
        foreach (string c in MaddieDb.DriveCategories) _category.Items.Add(c);
        _category.Width = 200;
        _category.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(search)) { _search.Text = search; if (_keywords != null) _category.SelectedIndex = 1; }

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4), WrapContents = false };
        top.Controls.Add(_category);
        top.Controls.Add(_search);
        var site = new Button { Text = "Open website", AutoSize = true };
        site.Click += (_, _) => Process.Start(new ProcessStartInfo(MaddieDb.AssetDriveUrl) { UseShellExecute = true });
        top.Controls.Add(site);
        top.Controls.Add(_add);
        top.Controls.Add(_status);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 520 };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_info);
        split.Panel2.Controls.Add(_preview);
        Controls.Add(split);
        Controls.Add(top);

        _add.Enabled = false;
        if (modDir == null) _status.Text = "Open a map inside a mod to add assets to it.";
        _category.SelectedIndexChanged += (_, _) => _ = LoadAsync();
        _search.TextChanged += (_, _) => Filter();
        _list.SelectedIndexChanged += (_, _) => _ = ShowAsync();
        _add.Click += async (_, _) => await AddAsync();
        Shown += (_, _) => _ = LoadAsync();
        FormClosed += (_, _) => _pick?.Cancel();
    }

    private string Cat => (string)_category.SelectedItem!;

    private async Task LoadAsync()
    {
        _status.Text = "Loading list…";
        string cat = Cat;
        try
        {
            List<DriveAsset> list;
            _why.Clear();
            if (cat == RecommendedLabel)
            {
                _status.Text = "Finding assets that fit your map…";
                var all = await Task.Run(() => MaddieDb.DriveAllAsync());
                var recs = await Task.Run(() => CreditAdvice.Recommend(all, _keywords!, _authors));
                list = recs.Select(r => r.Asset).ToList();
                foreach (var r in recs) _why[r.Asset] = r.Why;
            }
            else list = await Task.Run(() => MaddieDb.DriveListAsync(cat));
            if (IsDisposed || cat != Cat) return;
            _all = list;
            _status.Text = _modDir == null ? "Open a map inside a mod to add assets to it."
                : cat == RecommendedLabel ? $"{list.Count} picks, best match first." : $"{list.Count} files.";
            Filter();
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = "Couldn't load the Asset Drive: " + ex.Message; }
    }

    private void Filter()
    {
        string f = _search.Text.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var a in _all)
            if (f.Length == 0 || a.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || a.Author.Contains(f, StringComparison.OrdinalIgnoreCase)
                || a.Tags?.Any(t => t.Contains(f, StringComparison.OrdinalIgnoreCase)) == true)
            {
                _list.Items.Add(a);
                if (_list.Items.Count >= 3000) break;
            }
        _list.EndUpdate();
    }

    private async Task ShowAsync()
    {
        var sel = _list.SelectedItems.Cast<DriveAsset>().ToList();
        _add.Enabled = _modDir != null && sel.Count > 0;
        if (sel.Count != 1) { _info.Text = sel.Count > 1 ? $"{sel.Count} files selected." : ""; _preview.Image = null; return; }
        var a = sel[0];
        _pick?.Cancel();
        var cts = _pick = new CancellationTokenSource();
        _info.Text = Describe(a, null) + "\r\n\r\nLoading the author's readme…";
        try
        {
            byte[] png = await MaddieDb.DriveFileAsync(a.Id, cts.Token);
            if (cts.IsCancellationRequested || IsDisposed) return;
            try { using var ms = new MemoryStream(png); _preview.Image = new Bitmap(ms); } catch { _preview.Image = null; }
            string? readme = await MaddieDb.DriveTextAsync(a.Readme, cts.Token);
            if (cts.IsCancellationRequested || IsDisposed) return;
            _info.Text = Describe(a, readme);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) _info.Text = Describe(a, null) + "\r\n\r\nDownload failed: " + ex.Message; }
    }

    private string Describe(DriveAsset a, string? readme)
    {
        var s = $"{a.Name}\r\nBy: {a.CleanAuthor}\r\nFolder: {a.Folder}\r\nSize: {a.Width}x{a.Height}  ({a.Category})";
        if (_why.TryGetValue(a, out string? why)) s += "\r\n★ Recommended: " + why;
        if (a.Tags is { Count: > 0 }) s += "\r\nTags: " + string.Join(", ", a.Tags);
        if (a.Template != null) s += "\r\nTileset template: " + a.Template;
        if (a.Notes != null) s += "\r\n\r\nNotes: " + a.Notes.Replace("\n", "\r\n");
        if (readme != null) s += "\r\n\r\nReadme:\r\n" + readme.Replace("\r\n", "\n").Replace("\n", "\r\n");
        s += a.CreditOptional ? "\r\n\r\nThe author says no credit is needed; MT_Zenith still records it." : "\r\n\r\nCredit the author when you use this.";
        return s;
    }

    private async Task AddAsync()
    {
        if (_modDir == null) return;
        var sel = _list.SelectedItems.Cast<DriveAsset>().ToList();
        if (sel.Count == 0) return;
        string authors = string.Join(", ", sel.Select(a => a.CleanAuthor).Distinct());
        if (MessageBox.Show(this, $"Add {sel.Count} file(s) by {authors} to {_modName}?\n\nThey'll be credited to their author(s) in {CreditManifest.FileName} " +
                "and in your credits.txt. Make sure their terms (shown in the readme) allow your use.",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _add.Enabled = false;
        int n = 0;
        try
        {
            foreach (var a in sel)
            {
                _status.Text = $"Downloading {a.Name}…";
                byte[] data = await MaddieDb.DriveFileAsync(a.Id);
                string? readme = await MaddieDb.DriveTextAsync(a.Readme);
                string notes = string.Join("\n", new[] { a.Notes, readme }.Where(t => !string.IsNullOrWhiteSpace(t))).Trim();
                if (notes.Length > 800) notes = notes[..800] + "…";
                string rel = AssetCredits.Import(_modDir, AssetCredits.ImportFolder(a.Category, _modName) + a.Name, data, new RecordedCredit
                {
                    Author = a.CleanAuthor, Source = "Celeste Community Asset Drive", Url = MaddieDb.AssetDriveUrl, Notes = notes,
                });
                _imported(rel);
                n++;
            }
            string cat = sel.Select(a => a.Category).Distinct().Count() == 1 ? sel[0].Category : "";
            string where = cat == "decals" ? $"Find them in the Decals tab under decals/{_modName}/."
                : cat is "fgtilesets" or "bgtilesets" ? "Add them to your tileset xml to use them."
                : cat == "stylegrounds" ? $"Use them as parallax textures: bgs/{_modName}/…" : "";
            _status.Text = $"Added {n} file(s) with credit. {where}";
        }
        catch (Exception ex) { _status.Text = $"Added {n}; failed: {ex.Message}"; }
        finally { _add.Enabled = true; }
    }
}
