using System.Globalization;

namespace CELMapViewer;

/// <summary>Room properties dialog, fillers, the project manager, presets and preset groups, and third-party Lönn scripts.</summary>
public sealed partial class MainForm
{
    // ---- room properties ----

    private void EditRoomProperties(Room? room)
    {
        if (_map == null || room == null) { Log("Select a room first."); return; }
        using var f = new RoomPropertiesForm(room.El, creating: false,
            name => _canvas.Rooms.Any(r => r.El != room.El && string.Equals(r.Name, name.StartsWith("lvl_") ? name[4..] : name, StringComparison.OrdinalIgnoreCase)));
        if (f.ShowDialog(this) != DialogResult.OK) return;
        PushUndo();
        string name = f.RoomName.StartsWith("lvl_") ? f.RoomName[4..] : f.RoomName;
        if (name != room.Name) room.El.Set("name", (room.RawName.StartsWith("lvl_") ? "lvl_" : "") + name);
        var nb = f.Bounds8;
        if (nb != room.Bounds)
        {
            // keep content where it is in the world when the size changes, then move everything to the new position
            var sized = new Rectangle(room.X, room.Y, nb.Width, nb.Height);
            if (sized.Size != room.Bounds.Size) MapModel.ResizeRoom(room, sized);
            room.El.Set("x", nb.X);
            room.El.Set("y", nb.Y);
        }
        f.ApplyTo(room.El);
        if (f.WantsCheckpoint) AddCheckpoint(room.El);
        room.InvalidateRender();
        RebuildRooms(keepView: true);
        if (_canvas.Rooms.FirstOrDefault(r => r.El == room.El) is { } nr) { _canvas.Select(Selection.OfRoom(nr)); SelectRoomInList(nr); }
        MarkDirty();
        Log($"Updated room {name}: camera offset {room.El.Num("cameraOffsetX").ToString(CultureInfo.InvariantCulture)}, {room.El.Num("cameraOffsetY").ToString(CultureInfo.InvariantCulture)}.");
    }

    /// <summary>Adds a checkpoint entity near the room's spawn (or its bottom-left) when it has none.</summary>
    private void AddCheckpoint(BinElement level)
    {
        if (_map == null) return;
        var ents = level.Child("entities") ?? new BinElement("entities");
        if (!level.Children.Contains(ents)) level.Children.Add(ents);
        if (ents.Children.Any(e => e.Name == "checkpoint")) return;
        var spawn = ents.Children.FirstOrDefault(MapModel.IsSpawn);
        var cp = new BinElement("checkpoint");
        cp.Set("id", MapModel.NextId(_map));
        cp.Set("x", spawn != null ? spawn.Int("x") + 16 : 24);
        cp.Set("y", spawn != null ? spawn.Int("y") : Math.Max(8, level.Int("height") - 24));
        ents.Children.Add(cp);
    }

    // ---- fillers ----

    private void ConvertRoomToFiller(Room? room)
    {
        if (_map == null || room == null) { Log("Select a room first."); return; }
        int ents = room.EntitiesEl.Children.Count + room.TriggersEl.Children.Count;
        string warn = ents > 0 ? $"\n\nIts {ents} entities/triggers, background tiles and decals are dropped (Ctrl+Z restores them)." : "";
        if (MessageBox.Show(this, $"Turn the foreground tiles of {room.Name} into filler rectangles and remove the room?{warn}",
                AppInfo.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        PushUndo();
        int n = Fillers.RoomToFiller(_map, room);
        _canvas.Select(null);
        RebuildRooms(keepView: true);
        MarkDirty();
        Log($"Converted {room.Name} into {n} filler rectangle(s).");
    }

    private void ConvertFillersToRooms()
    {
        if (_map == null) return;
        int count = Fillers.Rects(_map).Count;
        if (count == 0) { Log("This map has no fillers."); return; }
        PushUndo();
        var added = Fillers.FillersToRooms(_map);
        RebuildRooms(keepView: true);
        MarkDirty();
        Log($"Converted {added.Count} filler(s) into rooms named _filler_….");
    }

    // ---- project manager ----

    private string? CurrentProject =>
        _settings.ProjectDir is { } d && Directory.Exists(d) ? d : _path != null ? MapModel.ModDirOf(_path) : null;

    private ToolStripMenuItem BuildProjectMenu()
    {
        var menu = new ToolStripMenuItem("Pro&ject");
        var campaigns = new ToolStripMenuItem("Choose &Campaign");
        var maps = new ToolStripMenuItem("Choose &Map");
        menu.DropDownItems.Add(campaigns);
        menu.DropDownItems.Add(maps);
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("New &Empty Project…", null, (_, _) => NewProject(standard: false)));
        menu.DropDownItems.Add(new ToolStripMenuItem("New &Standard Project…", null, (_, _) => NewProject(standard: true)));
        menu.DropDownItems.Add(new ToolStripMenuItem("&Open Project…", null, (_, _) => OpenProject()));
        menu.DropDownItems.Add(new ToolStripMenuItem("&Resync Open Project", null, (_, _) => ResyncProject()));
        menu.DropDownItems.Add(new ToolStripMenuItem("New map in &campaign…", null, (_, _) => NewMapInProject()));
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("&Zip Mod…", null, (_, _) => ZipProject()));
        menu.DropDownItems.Add(new ToolStripMenuItem("Open project &folder", null, (_, _) =>
        {
            if (CurrentProject is { } p) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
        }));
        menu.DropDownOpening += (_, _) =>
        {
            campaigns.DropDownItems.Clear();
            maps.DropDownItems.Clear();
            string? project = CurrentProject;
            menu.ToolTipText = project ?? "No project open";
            if (project == null)
            {
                campaigns.DropDownItems.Add(new ToolStripMenuItem("(no project: open one or a map in a mod)") { Enabled = false });
                maps.DropDownItems.Add(new ToolStripMenuItem("(no project)") { Enabled = false });
                return;
            }
            var all = ProjectManager.Campaigns(project);
            string? currentCampaign = _path != null ? all.FirstOrDefault(kv => kv.Value.Any(m => string.Equals(m, _path, StringComparison.OrdinalIgnoreCase))).Key : null;
            foreach (var (campaign, list) in all)
            {
                var item = new ToolStripMenuItem($"{campaign}  ({list.Count})") { Checked = campaign == currentCampaign };
                foreach (string m in list) item.DropDownItems.Add(MapItem(m));
                campaigns.DropDownItems.Add(item);
            }
            foreach (string m in all.Where(kv => currentCampaign == null || kv.Key == currentCampaign).SelectMany(kv => kv.Value))
                maps.DropDownItems.Add(MapItem(m));
            if (campaigns.DropDownItems.Count == 0) campaigns.DropDownItems.Add(new ToolStripMenuItem("(no maps in Maps/)") { Enabled = false });
            if (maps.DropDownItems.Count == 0) maps.DropDownItems.Add(new ToolStripMenuItem("(no maps)") { Enabled = false });
        };
        return menu;

        ToolStripMenuItem MapItem(string path) => new(Path.GetFileNameWithoutExtension(path), null, (_, _) =>
        {
            if (ConfirmDiscard()) Open(path);
        }) { Checked = string.Equals(path, _path, StringComparison.OrdinalIgnoreCase), ToolTipText = path };
    }

    private void NewProject(bool standard)
    {
        if (GameAssets.ModsDir is not { } mods) { Log("Celeste's Mods folder was not found. Use File ▸ Set Celeste folder… first."); return; }
        if (!ConfirmDiscard()) return;
        var values = AskFields(standard ? "New standard project" : "New empty project",
            ("Mod name", "MyMod"), ("Your name (map folder)", Environment.UserName), ("Campaign", "MyCampaign"), ("First map", "1-Map"));
        if (values == null) return;
        try
        {
            string map = ProjectManager.Create(mods, values[0], values[1], values[2], values[3], standard);
            _settings.ProjectDir = Path.Combine(mods, ProjectManager.Clean(values[0]));
            _settings.Save();
            GameAssets.Rescan();
            Open(map);
            Log($"Created {(standard ? "standard" : "empty")} project {_settings.ProjectDir} with {Path.GetFileName(map)}.");
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { Log("Could not create the project: " + e.Message); }
    }

    private void NewMapInProject()
    {
        if (CurrentProject is not { } project) { Log("Open a project first."); return; }
        string user = "", campaign = "";
        if (_path != null && Path.GetRelativePath(Path.Combine(project, "Maps"), Path.GetDirectoryName(_path)!).Split('\\', '/') is { Length: >= 2 } parts) { user = parts[0]; campaign = parts[1]; }
        var values = AskFields("New map", ("Your name (map folder)", user.Length > 0 ? user : Environment.UserName), ("Campaign", campaign.Length > 0 ? campaign : "MyCampaign"), ("Map name", "2-Map"));
        if (values == null || !ConfirmDiscard()) return;
        try
        {
            string map = ProjectManager.Create(Path.GetDirectoryName(project)!, Path.GetFileName(project), values[0], values[1], values[2], standard: false);
            Open(map);
            Log($"Created {map}.");
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { Log("Could not create the map: " + e.Message); }
    }

    private void OpenProject()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose a mod folder (the one with everest.yaml and Maps/)", UseDescriptionForTitle = true };
        if (CurrentProject is { } p) dlg.InitialDirectory = p;
        else if (GameAssets.ModsDir is { } m) dlg.InitialDirectory = m;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.ProjectDir = dlg.SelectedPath;
        _settings.Save();
        var all = ProjectManager.Campaigns(dlg.SelectedPath);
        Log($"Project: {dlg.SelectedPath} ({all.Count} campaign(s), {all.Sum(kv => kv.Value.Count)} map(s)). Pick a map from Project ▸ Choose Map.");
        if (_map == null && all.Values.SelectMany(v => v).FirstOrDefault() is { } first) Open(first);
    }

    private void ResyncProject()
    {
        GameAssets.Rescan();
        LoennScripts.Forget();
        ReloadAssets();
        if (_path != null && !_dirty && File.Exists(_path)) Open(_path);
        Log($"Resynced {CurrentProject ?? "mods"}: rescanned mods, tilesets, sprites and scripts.");
    }

    private void ZipProject()
    {
        if (CurrentProject is not { } project) { Log("Open a project first."); return; }
        using var dlg = new SaveFileDialog { Filter = "Zip|*.zip", FileName = Path.GetFileName(project) + ".zip", InitialDirectory = Path.GetDirectoryName(project) };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (Path.GetFullPath(dlg.FileName).StartsWith(Path.GetFullPath(project).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        { Log("Save the zip outside the project folder."); return; }
        try
        {
            int n = ProjectManager.Zip(project, dlg.FileName);
            Log($"Zipped {n} file(s) to {dlg.FileName}." +
                (string.Equals(Path.GetDirectoryName(dlg.FileName), GameAssets.ModsDir, StringComparison.OrdinalIgnoreCase)
                    ? " Note: Everest loads both the folder and the zip if they sit side by side in Mods." : ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("Zip failed: " + e.Message); }
    }

    /// <summary>A small form of labelled text boxes. Returns the values, or null when cancelled.</summary>
    private string[]? AskFields(string title, params (string Label, string Value)[] fields)
    {
        using var f = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10) };
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) });
            var t = new TextBox { Text = value, Width = 260 };
            boxes.Add(t);
            grid.Controls.Add(t);
        }
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        f.Controls.Add(grid); f.Controls.Add(buttons);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? boxes.Select(b => b.Text.Trim()).ToArray() : null;
    }

    // ---- presets & preset groups ----

    private ToolStripMenuItem BuildPresetsMenu()
    {
        var menu = new ToolStripMenuItem("Pre&sets");
        var entities = new ToolStripMenuItem("&Entities");
        var triggers = new ToolStripMenuItem("&Triggers");
        var groups = new ToolStripMenuItem("Preset &Groups");
        var delete = new ToolStripMenuItem("&Delete preset");
        menu.DropDownItems.Add(new ToolStripMenuItem("&Save selection as preset…", null, (_, _) => SavePreset(group: false)));
        menu.DropDownItems.Add(new ToolStripMenuItem("Save selection as preset &group…", null, (_, _) => SavePreset(group: true)));
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.AddRange(new ToolStripItem[] { entities, triggers, groups, new ToolStripSeparator(), delete });
        menu.DropDownOpening += (_, _) =>
        {
            foreach (var m in new[] { entities, triggers, groups, delete }) m.DropDownItems.Clear();
            foreach (var p in PresetStore.All.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var target = p.Kind switch { "entity" => entities, "trigger" => triggers, _ => groups };
                int n = PresetStore.Items(p)?.Count ?? 0;
                target.DropDownItems.Add(new ToolStripMenuItem(p.Kind == "group" ? $"{p.Name}  ({n} items)" : p.Name, null, (_, _) => PlacePreset(p))
                    { ToolTipText = "Places the preset at the mouse (or the view centre) in the room under it" });
                delete.DropDownItems.Add(new ToolStripMenuItem($"{p.KindLabel} ▸ {p.Name}", null, (_, _) =>
                {
                    if (MessageBox.Show(this, $"Delete preset '{p.Name}'?", AppInfo.Title, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    PresetStore.All.Remove(p);
                    PresetStore.Save();
                }));
            }
            foreach (var m in new[] { entities, triggers, groups, delete })
                if (m.DropDownItems.Count == 0) m.DropDownItems.Add(new ToolStripMenuItem("(none yet: select items and save them)") { Enabled = false });
        };
        return menu;
    }

    private void SavePreset(bool group)
    {
        var items = SelectedItemsOrPrimary().Where(s => !s.IsNode && s.El != null).ToList();
        if (items.Count == 0) { Log("Select entities, triggers or decals first (Shift+click or drag to select several)."); return; }
        string suggestion = items.Count == 1 ? (items[0].Decal ? Path.GetFileName(items[0].El!.Str("texture")) : items[0].El!.Name) : $"group of {items.Count}";
        string? name = Prompt(group || items.Count > 1 ? $"Name for the preset group ({items.Count} items)" : "Name for the preset", suggestion);
        if (string.IsNullOrWhiteSpace(name)) return;
        var world = items.Select(s =>
        {
            var c = s.El!.Clone();
            Move(c, s.Room.X, s.Room.Y);
            return (ItemClipboard.LayerOf(s), c);
        }).ToList();
        var preset = PresetStore.Make(name.Trim(), world, group);
        PresetStore.Add(preset);
        Log($"Saved {preset.KindLabel.ToLowerInvariant()} preset '{preset.Name}' ({world.Count} item(s)).");
    }

    private void PlacePreset(Preset p)
    {
        if (_map == null) { Log("Open a map first."); return; }
        var items = PresetStore.Items(p);
        if (items == null || items.Count == 0) { Log($"Preset '{p.Name}' is empty or damaged."); return; }
        PlaceItems(items, null, $"Placed preset '{p.Name}':");
    }

    // ---- third-party Lönn scripts ----

    private ToolStripMenuItem BuildScriptsMenu()
    {
        var menu = new ToolStripMenuItem("Scrip&ts");
        menu.DropDownItems.Add(new ToolStripMenuItem("(loading…)") { Enabled = false });
        menu.DropDownOpening += (_, _) =>
        {
            menu.DropDownItems.Clear();
            var current = new ToolStripMenuItem("Run on &Current Room", null, (_, _) => { _settings.ScriptsAllRooms = false; _settings.Save(); }) { Checked = !_settings.ScriptsAllRooms };
            var all = new ToolStripMenuItem("Run on &All Rooms", null, (_, _) => { _settings.ScriptsAllRooms = true; _settings.Save(); }) { Checked = _settings.ScriptsAllRooms };
            menu.DropDownItems.Add(current);
            menu.DropDownItems.Add(all);
            menu.DropDownItems.Add(new ToolStripSeparator());
            List<LoennScriptInfo> scripts;
            Cursor = Cursors.WaitCursor;
            try { scripts = LoennScripts.Discover(); }
            finally { Cursor = Cursors.Default; }
            if (scripts.Count == 0) menu.DropDownItems.Add(new ToolStripMenuItem("(no Loenn/scripts in installed mods)") { Enabled = false });
            foreach (var s in scripts)
                menu.DropDownItems.Add(new ToolStripMenuItem($"{s.DisplayName} [{s.Mod}]", null, (_, _) => RunLoennScript(s)) { ToolTipText = s.Tooltip });
            menu.DropDownItems.Add(new ToolStripSeparator());
            menu.DropDownItems.Add(new ToolStripMenuItem("&Rescan scripts", null, (_, _) => { LoennScripts.Forget(); GameAssets.Rescan(); Log("Scripts will be rescanned when the menu opens."); }));
        };
        return menu;
    }

    private void RunLoennScript(LoennScriptInfo info)
    {
        if (_map == null) { Log("Open a map first."); return; }
        bool allRooms = _settings.ScriptsAllRooms;
        var room = SelectedRoom();
        if (!allRooms && room == null && info.HasRun && !info.HasPrerun) { Log("Select a room first, or switch Scripts ▸ Run on All Rooms."); return; }
        var backup = _map.Root.Clone();
        PushUndo();
        try
        {
            var res = LoennScripts.Run(info, _map, room?.El, allRooms, AskScriptArgs);
            if (res == null) { _map.Root = backup; _undo.RemoveAt(_undo.Count - 1); return; }
            foreach (var line in res.Output.Take(30)) Log("  " + info.DisplayName + ": " + line);
            foreach (var r in _canvas.Rooms) { r.ReloadTiles(); r.InvalidateRender(); }
            RebuildRooms(keepView: true);
            bool any = res.RoomsChanged + res.RoomsAdded + res.RoomsRemoved > 0 || res.FillersChanged;
            if (any) MarkDirty(); else _undo.RemoveAt(_undo.Count - 1);
            Log($"{info.DisplayName} [{info.Mod}] on {(allRooms ? "all rooms" : room?.Name ?? "the map")}: " +
                (any ? $"{res.RoomsChanged} room(s) changed, {res.RoomsAdded} added, {res.RoomsRemoved} removed{(res.FillersChanged ? ", fillers changed" : "")}. Ctrl+Z undoes it."
                     : "no changes."));
        }
        catch (Exception e) when (e is MoonSharp.Interpreter.InterpreterException or InvalidOperationException or TimeoutException or InvalidCastException or NullReferenceException)
        {
            _map.Root = backup;
            _undo.RemoveAt(_undo.Count - 1);
            RebuildRooms(keepView: true);
            string msg = e is MoonSharp.Interpreter.InterpreterException ie ? ie.DecoratedMessage ?? ie.Message : e.Message;
            if (info.Code.Contains("$(")) msg += " (this script uses Lönn's $() syntax extension, which only Lönn understands)";
            Log($"{info.DisplayName} [{info.Mod}] failed, map unchanged: {msg}");
        }
    }

    /// <summary>Arguments dialog for a script's parameters. Null = cancelled.</summary>
    private Dictionary<string, object>? AskScriptArgs(string title, List<ScriptParam> ps)
    {
        var result = new Dictionary<string, object>();
        if (ps.Count == 0) return result;
        using var f = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10) };
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        var tips = new ToolTip();
        var getters = new List<(string Key, Func<object?> Get)>();
        foreach (var p in ps)
        {
            var label = new Label { Text = LoennScripts.Spaced(p.Key), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
            Control input;
            if (p.Value is bool b) { var c = new CheckBox { Checked = b, AutoSize = true }; input = c; getters.Add((p.Key, () => c.Checked)); }
            else if (p.Options.Count > 0)
            {
                var c = new ComboBox { Width = 260, DropDownStyle = p.Editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList };
                c.Items.AddRange(p.Options.Cast<object>().ToArray());
                string cur = Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "";
                if (!p.Options.Contains(cur)) c.Items.Insert(0, cur);
                c.SelectedItem = cur; c.Text = cur;
                input = c;
                object orig = p.Value;
                getters.Add((p.Key, () => Coerce(c.Text, orig)));
            }
            else
            {
                var t = new TextBox { Width = 260, Text = Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "" };
                input = t;
                object orig = p.Value;
                getters.Add((p.Key, () => Coerce(t.Text, orig)));
            }
            if (p.Tooltip != null) { tips.SetToolTip(label, p.Tooltip); tips.SetToolTip(input, p.Tooltip); }
            grid.Controls.Add(label);
            grid.Controls.Add(input);
        }
        var ok = new Button { Text = "Run", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        f.Controls.Add(grid); f.Controls.Add(buttons);
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK) return null;
        foreach (var (k, get) in getters) if (get() is { } v) result[k] = v;
        return result;

        static object Coerce(string text, object orig) => orig switch
        {
            int when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) => i,
            int or float or double when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
            _ => text
        };
    }
}
