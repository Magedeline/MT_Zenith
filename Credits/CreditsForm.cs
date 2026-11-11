using System.Diagnostics;

namespace CELMapViewer;

/// <summary>
/// Map ▸ Asset credits: everything the open map uses and who to credit for it (installed mods via Maddie480's
/// GameBanana database, community assets via the Asset Drive, the mapper's own work), with a credits.txt export.
/// </summary>
public sealed class CreditsForm : Form
{
    private readonly MapBin _map;
    private readonly string? _mapPath;
    private readonly Action _assetsChanged;
    private readonly Action<string> _log;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true,
    };
    private readonly Label _summary = new() { Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(6, 4, 6, 0) };
    private readonly CheckBox _todoOnly = new() { Text = "Only what needs credit", AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Top, Height = 200, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(40, 40, 48) };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true };
    private AssetCredits.ScanResult? _result;
    private int _previewSeq;
    private CancellationTokenSource? _cts;

    public CreditsForm(MapBin map, string? mapPath, Action assetsChanged, Action<string> log)
    {
        _map = map; _mapPath = mapPath; _assetsChanged = assetsChanged; _log = log;
        Text = "Asset credits — " + (mapPath == null ? "untitled" : Path.GetFileNameWithoutExtension(mapPath));
        Size = new Size(1100, 640);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;

        foreach (var (h, w) in new[] { ("Status", 130), ("Kind", 80), ("Asset", 280), ("Credit to", 150), ("From", 190), ("Uses", 50), ("Next step", 360) })
            _list.Columns.Add(h, w);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        Button B(string text, Action a, string tip = "")
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (_, _) => a();
            if (tip.Length > 0) new ToolTip().SetToolTip(b, tip);
            bar.Controls.Add(b);
            return b;
        }
        B("Refresh online data", () => _ = ScanAsync(true), "Re-download Maddie480's GameBanana mod database and Asset Drive lists.");
        B("Mark as my own work", () => Record(own: true), "Your own art: credited as an original asset of your map.");
        B("Credit someone…", () => Record(own: false), "Record who made the selected asset(s).");
        B("Confirm Asset Drive match", ConfirmDrive, "The file matches an Asset Drive file: record that creator as the author.");
        B("Open link", OpenLink);
        B("Find on Asset Drive", FindOnDrive, "Look for the selected asset (or similar ones) on the Asset Drive, to find who made it or a credited alternative.");
        B("Asset Drive…", () => OpenDrive(null), "Browse credited community assets on maddie480.ovh (with picks recommended for this map) and add them to your mod.");
        B("Import my own / other asset…", ImportFiles, "Bring your own PNGs into your mod, recording who made them.");
        B("Copy credits", () => { if (_result != null) Clipboard.SetText(CreditsText()); });
        B("Save credits.txt…", SaveCredits);
        bar.Controls.Add(_todoOnly);
        _todoOnly.CheckedChanged += (_, _) => Fill();

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_details);
        split.Panel2.Controls.Add(_preview);
        Controls.Add(split);
        Shown += (_, _) => { try { split.SplitterDistance = Math.Max(200, split.Width - 330); } catch { } };
        _list.SelectedIndexChanged += (_, _) => _ = PreviewAsync();
        Controls.Add(_summary);
        Controls.Add(bar);
        _list.DoubleClick += (_, _) => OpenLink();
        Shown += (_, _) => _ = ScanAsync(false);
        FormClosed += (_, _) => _cts?.Cancel();
    }

    private string MapName => _mapPath == null ? "Untitled map" : Path.GetFileNameWithoutExtension(_mapPath);
    private string CreditsText() => AssetCredits.CreditsText(_result!, MapName);

    private async Task ScanAsync(bool refresh)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _summary.Text = refresh ? "Downloading Maddie480's mod database and Asset Drive lists…" : "Scanning the map (first run downloads Maddie480's mod database, ~15 MB)…";
        UseWaitCursor = true;
        try
        {
            var r = await Task.Run(() => AssetCredits.ScanAsync(_map, _mapPath, true, refresh, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || IsDisposed) return;
            _result = r;
            Fill();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) _summary.Text = "Scan failed: " + ex.Message; }
        finally { if (!IsDisposed) UseWaitCursor = false; }
    }

    private static string StatusText(CreditOrigin o) => o switch
    {
        CreditOrigin.Mod => "Mod (credit it)",
        CreditOrigin.Borrowed => "Credited",
        CreditOrigin.Own => "Your own work",
        CreditOrigin.PossiblyBorrowed => "⚠ Asset Drive match?",
        CreditOrigin.Unrecorded => "⚠ No credit recorded",
        CreditOrigin.Vanilla => "Celeste",
        _ => "⚠ Missing",
    };

    private void Fill()
    {
        if (_result == null) return;
        var r = _result;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in r.Entries)
        {
            if (_todoOnly.Checked && !e.NeedsAttention) continue;
            var it = new ListViewItem(new[] { StatusText(e.Origin), e.Kind, e.Asset, e.CreditTo, e.Source, e.Uses.ToString(), CreditAdvice.NextStep(e) }) { Tag = e };
            if (e.NeedsAttention) it.ForeColor = e.Origin == CreditOrigin.Missing ? Themes.Current.Error : Themes.Current.Warn;
            else if (e.Origin == CreditOrigin.Vanilla) it.ForeColor = Themes.Current.Muted;
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        int todo = r.Entries.Count(e => e.NeedsAttention);
        int mods = r.Entries.Where(e => e.Origin == CreditOrigin.Mod).Select(e => e.Source).Distinct().Count();
        int people = r.Entries.Where(e => e.Origin == CreditOrigin.Borrowed).Select(e => e.CreditTo).Distinct().Count();
        string online = r.OnlineData
            ? $"Mod data from maddie480.ovh (updated {MaddieDb.CacheTime("mod_search_database.yaml"):g})."
            : "Offline: mod authors couldn't be looked up (Refresh online data when connected).";
        _summary.Text = $"{r.Entries.Count} assets/entity types: {mods} mods and {people} other creators to credit"
                      + (todo > 0 ? $", {todo} still need a credit decision." : ", everything is credited.")
                      + (r.ModDir == null ? "  (Map isn't inside a mod's Maps folder: own-asset credits can't be recorded.)" : "")
                      + "\n" + online;
        _summary.ForeColor = todo > 0 ? Themes.Current.Warn : Themes.Current.Text;
    }

    /// <summary>The texture an entry shows (decal, styleground, tileset, or an entity's Lönn sprite), loaded off the UI thread.</summary>
    private static Tex? TextureOf(CreditEntry e)
    {
        string? path = e.Kind switch
        {
            "Decal" => "decals/" + e.Asset,
            "Styleground" or "Tileset" => e.Asset,
            "Entity" => PluginCatalog.Entity(e.Asset)?.Texture,
            _ => null,
        };
        return path == null ? null : GameAssets.Gameplay.GetFirstFrame(path);
    }

    private async Task PreviewAsync()
    {
        int seq = ++_previewSeq;
        var e = Selected().FirstOrDefault();
        if (e == null) { _preview.Image = null; _details.Text = ""; return; }
        _details.Text = Details(e, null);
        Tex? tex = null;
        try { tex = await Task.Run(() => TextureOf(e)); } catch { }
        if (seq != _previewSeq || IsDisposed) return;
        _preview.Image = tex?.Bitmap;
        _details.Text = Details(e, tex);
    }

    private static string Details(CreditEntry e, Tex? tex)
    {
        var s = $"{e.Kind}: {e.Asset}\r\nStatus: {StatusText(e.Origin)}";
        if (e.CreditTo.Length > 0) s += $"\r\nCredit to: {e.CreditTo}";
        if (e.Source.Length > 0) s += $"\r\nFrom: {e.Source}";
        if (e.Url.Length > 0) s += $"\r\nLink: {e.Url}  (double-click the row to open)";
        if (e.File != null) s += $"\r\nFile: {e.File}";
        s += $"\r\nUsed {e.Uses}×";
        if (tex != null) s += $"\r\nSize: {tex.W}x{tex.H}";
        s += $"\r\n\r\n★ Recommended: {CreditAdvice.NextStep(e)}";
        if (e.Note.Length > 0) s += $"\r\n\r\n{e.Note.Replace("\n", "\r\n")}";
        return s;
    }

    private List<CreditEntry> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (CreditEntry)i.Tag!).ToList();

    private bool NeedModDir()
    {
        if (_result?.ModDir != null) return true;
        MessageBox.Show(this, "This map isn't inside a mod (…/YourMod/Maps/…), so there's no mod folder to record credits in.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void Record(bool own)
    {
        var sel = Selected().Where(e => e.File != null).ToList();
        if (sel.Count == 0)
        {
            MessageBox.Show(this, "Select assets that are in your own mod (status \"No credit recorded\", \"Asset Drive match?\", \"Credited\" or \"Your own work\"). " +
                "Mods and Celeste assets are credited automatically.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!NeedModDir()) return;
        RecordedCredit credit;
        bool folder = false;
        if (own) credit = new RecordedCredit { Own = true, Source = "Own work" };
        else
        {
            var first = sel[0];
            var existing = first.Origin is CreditOrigin.Borrowed or CreditOrigin.PossiblyBorrowed
                ? new RecordedCredit { Author = first.CreditTo, Source = first.Source, Url = first.Url, Notes = first.Origin == CreditOrigin.Borrowed ? first.Note : "" }
                : null;
            using var dlg = new CreditDialog(sel.Count == 1 ? $"Credit for {sel[0].Asset}" : $"Credit for {sel.Count} assets", false, true, existing);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            credit = dlg.Credit;
            folder = dlg.WholeFolder;
        }
        var m = CreditManifest.Load(_result!.ModDir);
        foreach (var e in sel)
        {
            string path = folder ? e.File![..(e.File!.LastIndexOf('/') + 1)] : e.File!;
            m.Set(new RecordedCredit { Path = path, Own = credit.Own, Author = credit.Author, Source = credit.Source, Url = credit.Url, Notes = credit.Notes });
        }
        m.Save(_result.ModDir!);
        _log($"Recorded credits for {sel.Count} asset(s) in {CreditManifest.FileName}.");
        _ = ScanAsync(false);
    }

    private void ConfirmDrive()
    {
        var sel = Selected().Where(e => e.Origin == CreditOrigin.PossiblyBorrowed && e.File != null).ToList();
        if (sel.Count == 0) { MessageBox.Show(this, "Select rows marked \"Asset Drive match?\".", Text); return; }
        if (!NeedModDir()) return;
        var m = CreditManifest.Load(_result!.ModDir);
        foreach (var e in sel)
            m.Set(new RecordedCredit { Path = e.File!, Author = e.CreditTo, Source = "Celeste Community Asset Drive", Url = MaddieDb.AssetDriveUrl });
        m.Save(_result.ModDir!);
        _ = ScanAsync(false);
    }

    private void OpenLink()
    {
        var e = Selected().FirstOrDefault();
        if (e == null || e.Url.Length == 0 || !Uri.TryCreate(e.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return;
        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    private void FindOnDrive()
    {
        var e = Selected().FirstOrDefault();
        string? q = e == null ? null : e.Asset.Replace('\\', '/').Split('/').Last().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_');
        OpenDrive(string.IsNullOrWhiteSpace(q) ? null : q);
    }

    private void OpenDrive(string? search)
    {
        var authors = new HashSet<string>(_result?.Entries.Where(x => x.Origin == CreditOrigin.Borrowed).Select(x => x.CreditTo) ?? Enumerable.Empty<string>(),
                                          StringComparer.OrdinalIgnoreCase);
        using var f = new AssetDriveForm(_result?.ModDir, ModFolderName, rel => { _assetsChanged(); _log("Imported " + rel); },
                                         CreditAdvice.MapKeywords(_map), authors, search);
        f.ShowDialog(this);
        _ = ScanAsync(false);
    }

    private string ModFolderName => _result?.ModDir == null ? "MyMod" : AssetCredits.SafeSegment(Path.GetFileName(_result.ModDir.TrimEnd('\\', '/')));

    private void ImportFiles()
    {
        if (!NeedModDir()) return;
        using var open = new OpenFileDialog { Filter = "PNG images|*.png", Multiselect = true, Title = "Import images into your mod" };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        using var dlg = new CreditDialog($"Importing {open.FileNames.Length} image(s) into {ModFolderName}.", true, false);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string folder = AssetCredits.ImportFolder(dlg.Category, ModFolderName);
        foreach (string f in open.FileNames)
        {
            var c = dlg.Credit;
            string rel = AssetCredits.Import(_result!.ModDir!, folder + Path.GetFileName(f), File.ReadAllBytes(f), c);
            _log("Imported " + rel + (c.Own ? " (own work)" : $" (by {c.Author})"));
        }
        if (dlg.Category is "fgtilesets" or "bgtilesets")
            MessageBox.Show(this, $"Tileset images were copied to {folder}. Add them to your ForegroundTiles.xml / BackgroundTiles.xml to use them.", Text);
        _assetsChanged();
        _ = ScanAsync(false);
    }

    private void SaveCredits()
    {
        if (_result == null) return;
        if (_result.Entries.Any(e => e.NeedsAttention) &&
            MessageBox.Show(this, "Some assets still have no confirmed credit; they'll be listed under TODO. Save anyway?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        using var save = new SaveFileDialog
        {
            Filter = "Text|*.txt", FileName = "credits.txt",
            InitialDirectory = _result.ModDir ?? (_mapPath == null ? "" : Path.GetDirectoryName(_mapPath)),
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(save.FileName, CreditsText());
        _log("Saved " + save.FileName);
    }
}
