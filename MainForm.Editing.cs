using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CELMapViewer;

/// <summary>
/// Rysy/Lönn-style item editing: clipboard, multi-item delete/duplicate/nudge/flip, nodes, decal browser,
/// the field-aware property grid and the stylegrounds/metadata editor.
/// </summary>
public sealed partial class MainForm
{
    // ---- selection helpers ----

    private bool TextFocused()
    {
        if (_attrs.IsCurrentCellInEditMode) return true;
        var c = ActiveControl;
        while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
        return c is TextBoxBase or ComboBox;
    }

    private List<Selection> SelectedItemsOrPrimary() =>
        _canvas.SelectedItems.Count > 0 ? _canvas.SelectedItems.ToList()
        : _canvas.Selected is { El: not null } s ? new() { s } : new();

    private static PluginDef? DefOf(Selection s) =>
        s.El == null || s.Decal ? null : s.Trigger ? PluginCatalog.Trigger(s.El.Name) : PluginCatalog.Entity(s.El.Name);

    private static void Move(BinElement el, float dx, float dy, bool withNodes = true)
    {
        MapCanvas.SetNum(el, "x", el.Num("x") + dx);
        MapCanvas.SetNum(el, "y", el.Num("y") + dy);
        if (withNodes)
            foreach (var n in MapCanvas.Nodes(el)) { MapCanvas.SetNum(n, "x", n.Num("x") + dx); MapCanvas.SetNum(n, "y", n.Num("y") + dy); }
    }

    private void AfterItemEdit(IEnumerable<Selection> touched)
    {
        foreach (var r in touched.Where(s => s.Decal).Select(s => s.Room).Distinct()) r.InvalidateRender();
        MarkDirty();
        LoadGrid();
        _canvas.Invalidate();
    }

    private void SelectAllInRoom()
    {
        if (SelectedRoom() is not { } room) return;
        _canvas.SelectMany(_canvas.ItemsOf(room));
        _status.Text = $"Selected {_canvas.SelectedItems.Count} item(s) in {room.Name}";
    }

    // ---- delete / duplicate / nudge / flip / nodes ----

    private void DeleteSelected()
    {
        if (_canvas.Selected is not { } primary) return;
        var items = SelectedItemsOrPrimary();
        if (items.Count == 0) { DeleteRoom(primary.Room); return; }
        PushUndo();
        int kept = 0;
        foreach (var s in items.OrderByDescending(s => s.Node))
        {
            if (s.IsNode)
            {
                var nodes = MapCanvas.Nodes(s.El!).ToList();
                if (nodes.Count <= (DefOf(s)?.MinNodes ?? 0) || s.NodeEl is not { } n) { kept++; continue; }
                s.El!.Children.Remove(n);
            }
            else s.Container?.Children.Remove(s.El!);
        }
        if (kept > 0) _status.Text = $"Kept {kept} node(s): the entity needs at least that many";
        _canvas.Select(null);
        AfterItemEdit(items);
    }

    private void Duplicate()
    {
        if (_canvas.Selected is not { } primary || _map == null) return;
        var items = SelectedItemsOrPrimary().Where(s => !s.IsNode).ToList();
        if (items.Count == 0) { if (primary.IsRoom) DuplicateRoom(primary.Room); return; }
        PushUndo();
        int id = MapModel.NextId(_map);
        var copies = new List<Selection>();
        foreach (var s in items)
        {
            var copy = s.El!.Clone();
            if (!s.Decal) copy.Set("id", id++);
            Move(copy, 8, 8);
            s.Container!.Children.Add(copy);
            copies.Add(s with { El = copy });
        }
        _canvas.SelectMany(copies);
        AfterItemEdit(copies);
    }

    private void Nudge(int dx, int dy)
    {
        if (_canvas.Selected is not { } primary) return;
        PushUndo();
        var items = SelectedItemsOrPrimary();
        if (items.Count == 0)
        {
            primary.Room.El.Set("x", primary.Room.X + (dx == 0 ? 0 : Math.Sign(dx) * 8));
            primary.Room.El.Set("y", primary.Room.Y + (dy == 0 ? 0 : Math.Sign(dy) * 8));
            MarkDirty();
            LoadGrid();
            _canvas.Invalidate();
            return;
        }
        foreach (var s in items)
        {
            if (s.NodeEl is { } n) Move(n, dx, dy, false);
            else Move(s.El!, dx, dy);
        }
        AfterItemEdit(items);
    }

    private static readonly (string A, string B)[] HorizontalPairs = { ("Left", "Right"), ("left", "right") };
    private static readonly (string A, string B)[] VerticalPairs = { ("Up", "Down"), ("up", "down"), ("Top", "Bottom") };

    /// <summary>Flips decals (scale sign) and entities/triggers (Left↔Right / Up↔Down name variant, mirrored nodes).</summary>
    private void FlipSelected(bool horizontal)
    {
        var items = SelectedItemsOrPrimary().Where(s => !s.IsNode).ToList();
        if (items.Count == 0) return;
        PushUndo();
        int renamed = 0;
        foreach (var s in items)
        {
            var el = s.El!;
            if (s.Decal)
            {
                string k = horizontal ? "scaleX" : "scaleY";
                el.Set(k, -el.Num(k, 1));
                continue;
            }
            foreach (var (a, b) in horizontal ? HorizontalPairs : VerticalPairs)
            {
                string? swapped = el.Name.EndsWith(a) ? el.Name[..^a.Length] + b : el.Name.EndsWith(b) ? el.Name[..^b.Length] + a : null;
                if (swapped == null) continue;
                if ((s.Trigger ? PluginCatalog.Trigger(swapped) : PluginCatalog.Entity(swapped)) != null) { el.Name = swapped; renamed++; }
                break;
            }
            var box = MapModel.Box(s.Room, el, s.Trigger);
            float c = horizontal ? box.X - s.Room.X + box.Width / 2 : box.Y - s.Room.Y + box.Height / 2;
            string axis = horizontal ? "x" : "y";
            foreach (var n in MapCanvas.Nodes(el)) MapCanvas.SetNum(n, axis, 2 * c - n.Num(axis));
        }
        _status.Text = $"Flipped {items.Count} item(s){(renamed > 0 ? $", {renamed} switched direction" : "")}";
        AfterItemEdit(items);
    }

    private void AddNode()
    {
        if (_canvas.Selected is not { El: { } el } s || s.Decal) return;
        var nodes = MapCanvas.Nodes(el).ToList();
        int max = DefOf(s)?.MaxNodes ?? -1;
        if (max >= 0 && nodes.Count >= max)
        {
            _status.Text = max == 0 ? $"{el.Name} has no nodes" : $"{el.Name} allows at most {max} node(s)";
            return;
        }
        PushUndo();
        var after = s.NodeEl ?? nodes.LastOrDefault() ?? el;
        var node = new BinElement("node");
        MapCanvas.SetNum(node, "x", after.Num("x") + 16);
        MapCanvas.SetNum(node, "y", after.Num("y"));
        int index = s.NodeEl != null ? el.Children.IndexOf(s.NodeEl) + 1 : el.Children.Count;
        el.Children.Insert(index, node);
        _canvas.Select(s with { Node = MapCanvas.Nodes(el).ToList().IndexOf(node) });
        MarkDirty();
    }

    // ---- clipboard ----

    private void CopySelected()
    {
        var items = SelectedItemsOrPrimary().Where(s => !s.IsNode).ToList();
        string json;
        if (items.Count > 0)
            json = ItemClipboard.Serialize(items.Select(s =>
            {
                var c = s.El!.Clone();
                Move(c, s.Room.X, s.Room.Y);   // clipboard positions are in world space
                return (ItemClipboard.LayerOf(s), c);
            }));
        else if (_canvas.Selected is { IsRoom: true } r) json = ItemClipboard.Serialize(new[] { ("room", r.Room.El.Clone()) });
        else return;
        try { Clipboard.SetText(json); } catch (Exception e) { Log("Copy failed: " + e.Message); return; }
        _status.Text = items.Count > 0 ? $"Copied {items.Count} item(s)" : "Copied room";
    }

    private void CutSelected()
    {
        if (_canvas.Selected is { IsRoom: true }) { CopySelected(); return; }
        CopySelected();
        DeleteSelected();
    }

    private static bool ClipboardHasItems()
    {
        try { return Clipboard.ContainsText() && Clipboard.GetText().Contains(ItemClipboard.Format); } catch { return false; }
    }

    private void Paste() => Paste(null);

    private void Paste(PointF? at)
    {
        if (_map == null) return;
        string text;
        try { text = Clipboard.GetText(); } catch { return; }
        var items = ItemClipboard.Deserialize(text);
        if (items == null || items.Count == 0) { _status.Text = "Clipboard has no map items"; return; }
        PlaceItems(items, at, "Pasted");
    }

    /// <summary>Adds items (entities, triggers, decals or whole rooms) at a world point (default: the mouse, else the view centre).</summary>
    private void PlaceItems(List<(string Layer, BinElement El)> items, PointF? at, string verb)
    {
        if (_map == null || items.Count == 0) return;
        if (at == null)
        {
            var p = _canvas.PointToClient(Cursor.Position);
            at = _canvas.ClientRectangle.Contains(p) ? _canvas.ToWorld(p) : _canvas.ViewCenter;
        }
        var w = at.Value;

        var rooms = items.Where(i => i.Layer == "room").Select(i => i.El).ToList();
        if (rooms.Count > 0)
        {
            PushUndo();
            int ox = rooms.Min(r => r.Int("x")), oy = rooms.Min(r => r.Int("y"));
            var added = rooms.Select(r => MapModel.DuplicateLevel(_map, r, MapModel.Snap8(w.X) + r.Int("x") - ox, MapModel.Snap8(w.Y) + r.Int("y") - oy)).ToList();
            RebuildRooms(keepView: true);
            if (_canvas.Rooms.FirstOrDefault(r => r.El == added[^1]) is { } nr) _canvas.Select(Selection.OfRoom(nr));
            MarkDirty();
            Log($"{verb} {added.Count} room(s).");
            return;
        }

        var room = _canvas.RoomAt(w) ?? SelectedRoom();
        if (room == null) { _status.Text = $"{verb}: point at a room or select one first"; return; }
        PushUndo();
        float minX = items.Min(i => i.El.Num("x")), minY = items.Min(i => i.El.Num("y"));
        float dx = MapModel.Snap8(w.X - room.X) - minX, dy = MapModel.Snap8(w.Y - room.Y) - minY;
        int id = MapModel.NextId(_map);
        var pasted = new List<Selection>();
        foreach (var (layer, el) in items)
        {
            Move(el, dx, dy);
            bool decal = layer is "fgdecal" or "bgdecal";
            if (!decal) el.Set("id", id++);
            var container = layer switch
            {
                "trigger" => room.TriggersEl,
                "fgdecal" => room.GetOrAdd("fgdecals"),
                "bgdecal" => room.GetOrAdd("bgdecals"),
                _ => room.EntitiesEl
            };
            container.Children.Add(el);
            pasted.Add(new Selection(room, el, layer == "trigger", decal));
        }
        _canvas.SelectMany(pasted);
        AfterItemEdit(pasted);
        _status.Text = $"{verb} {pasted.Count} item(s) into {room.Name}";
    }

    // ---- decal browser ----

    private readonly TextBox _decalSearch = new() { Dock = DockStyle.Top, PlaceholderText = "Search decals…" };
    private readonly ListBox _decalList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly PictureBox _decalPreview = new() { Dock = DockStyle.Bottom, Height = 120, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(40, 40, 48) };
    private List<string>? _decalNames;
    private bool _decalsLoading;

    private TabPage BuildDecalTab()
    {
        var page = new TabPage("Decals");
        page.Controls.Add(_decalList);
        page.Controls.Add(_decalPreview);
        page.Controls.Add(_decalSearch);
        page.Enter += (_, _) => EnsureDecals();
        _decalSearch.TextChanged += (_, _) => RefreshDecals();
        _decalList.SelectedIndexChanged += (_, _) =>
        {
            if (_decalList.SelectedItem is not string name) return;
            _canvas.PlaceDecal = name;
            _decalPreview.Image = RoomRenderJob.DecalTexture(name)?.Bitmap;
            SetTool(Tool.Decal);
        };
        return page;
    }

    private void EnsureDecals()
    {
        if (_decalNames != null || _decalsLoading) return;
        _decalsLoading = true;
        _decalList.Items.Add("Loading…");
        Task.Run(DecalNames).ContinueWith(t =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                _decalsLoading = false;
                _decalNames = t.IsCompletedSuccessfully ? t.Result : new();
                if (t.Exception != null) Log("Decal list failed: " + t.Exception.GetBaseException().Message);
                RefreshDecals();
            });
        });
    }

    /// <summary>Every decal in the vanilla atlas and installed mods, with animation frames collapsed to their base name.</summary>
    public static List<string> DecalNames()
    {
        const string modPrefix = "Graphics/Atlases/Gameplay/decals/";
        var raw = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in GameAssets.Gameplay.Keys("decals/")) raw.Add(k["decals/".Length..]);
        foreach (var (_, rel, _) in GameAssets.EnumerateModFiles("Graphics/Atlases/Gameplay/decals", ".png"))
            if (rel.StartsWith(modPrefix, StringComparison.OrdinalIgnoreCase)) raw.Add(rel[modPrefix.Length..^4]);
        return CollapseFrames(raw);
    }

    public static List<string> CollapseFrames(IEnumerable<string> names)
    {
        var list = names.ToList();
        var bases = list.GroupBy(n => n.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in list)
        {
            string b = n.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            result.Add(b != n && b.Length > 0 && !b.EndsWith('/') && (bases[b] > 1 || n.EndsWith("00")) ? b : n);
        }
        return result.ToList();
    }

    private void RefreshDecals()
    {
        if (_decalNames == null) return;
        string f = _decalSearch.Text.Trim();
        var keep = _decalList.SelectedItem;
        _decalList.BeginUpdate();
        _decalList.Items.Clear();
        foreach (var n in _decalNames)
            if (f.Length == 0 || n.Contains(f, StringComparison.OrdinalIgnoreCase)) _decalList.Items.Add(n);
        if (keep != null && _decalList.Items.Contains(keep)) _decalList.SelectedItem = keep;
        _decalList.EndUpdate();
    }

    // ---- field-aware property grid ----

    private static readonly Dictionary<string, FieldInfo> RoomFieldInfo = new()
    {
        ["windPattern"] = new FieldInfo { Editable = false, Options = PluginCatalog.WindPatterns.Select(p => new KeyValuePair<string, object>(p, p)).ToList() },
        ["c"] = new FieldInfo { FieldType = "integer" }
    };

    private void WireGrid()
    {
        _attrs.CellValueChanged += (_, e) => { if (e.ColumnIndex != 1) CommitGridRow(e.RowIndex); };
        _attrs.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_attrs.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell && _attrs.IsCurrentCellDirty)
                _attrs.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _attrs.DataError += (_, e) => e.ThrowException = false;
        _attrs.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 2 || _attrs.Rows[e.RowIndex].Cells[2] is not DataGridViewTextBoxCell cell) return;
            if (cell.Value is not string text || !IsColorRow(_attrs.Rows[e.RowIndex]) || PluginCatalog.ParseHex(text) is not { } col) return;
            using var dlg = new ColorDialog { Color = col, FullOpen = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _attrs.EndEdit();
            string hex = $"{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}";
            if (text.Trim().Length is 8 or 9) hex += text.Trim()[^2..];
            cell.Value = (text.TrimStart().StartsWith('#') ? "#" : "") + hex;
        };
        _attrs.UserDeletingRow += (_, e) =>
        {
            if (_canvas.Selected is not { } s || e.Row?.Cells[0].Value is not string key) return;
            if (s.El == null) { e.Cancel = true; return; }
            PushUndo();
            var targets = GridTargets(s);
            foreach (var t in targets) t.El!.Remove(key);
            AfterItemEdit(targets);
        };
    }

    private bool IsColorRow(DataGridViewRow row) =>
        row.Tag is FieldInfo { FieldType: "color" } ||
        (row.Cells[0].Value as string)?.Contains("color", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Items a grid edit applies to: every selected item of the same kind and name as the primary one.</summary>
    private List<Selection> GridTargets(Selection s) =>
        s.El == null ? new() { s }
        : SelectedItemsOrPrimary().Where(x => x.El!.Name == s.El.Name && x.Decal == s.Decal && x.Trigger == s.Trigger)
                                  .GroupBy(x => x.El).Select(g => g.First()).DefaultIfEmpty(s).ToList();

    private void LoadGrid()
    {
        _loadingGrid = true;
        _attrs.Rows.Clear();
        if (_canvas.Selected is not { } s)
        {
            _selLabel.Text = "Nothing selected";
            _attrs.AllowUserToAddRows = false;
            _loadingGrid = false;
            return;
        }
        var target = s.El ?? s.Room.El;
        var def = DefOf(s);
        var fields = s.IsRoom ? RoomFieldInfo : def?.Fields;
        var order = def?.FieldOrder;
        var attrs = target.Attrs.AsEnumerable();
        if (order is { Count: > 0 })
            attrs = attrs.OrderBy(a => order.IndexOf(a.Key) is var i && i >= 0 ? i : int.MaxValue);

        foreach (var a in attrs)
        {
            var row = _attrs.Rows[_attrs.Rows.Add(a.Key, a.Type.ToString(), FormatValue(a.Value))];
            var fi = fields?.GetValueOrDefault(a.Key);
            row.Tag = fi;
            if (a.Value is bool bv) row.Cells[2] = new DataGridViewCheckBoxCell { Value = bv };
            else if (fi is { Options.Count: > 0 } && !fi.Editable)
            {
                var combo = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox };
                string current = FormatValue(a.Value);
                string? label = null;
                foreach (var (l, v) in fi.Options)
                {
                    combo.Items.Add(l);
                    if (label == null && FormatValue(v) == current) label = l;
                }
                if (label == null) combo.Items.Add(label = current);
                combo.Value = label;
                row.Cells[2] = combo;
            }
            else if (fi is { Options.Count: > 0 })
                row.Cells[2].ToolTipText = "Options: " + string.Join(", ", fi.Options.Select(o => o.Key == FormatValue(o.Value) ? o.Key : $"{o.Key} = {FormatValue(o.Value)}"));
            if (a.Value is string str && IsColorRow(row) && PluginCatalog.ParseHex(str) is { } c)
            {
                row.Cells[2].Style.BackColor = Color.FromArgb(255, c);
                row.Cells[2].Style.ForeColor = c.GetBrightness() > 0.5f ? Color.Black : Color.White;
                row.Cells[2].ToolTipText = "Double-click to pick a color";
            }
        }

        if (s.IsRoom)
            _selLabel.Text = $"Room: {s.Room.Name}  ({s.Room.W}x{s.Room.H}, {s.Room.EntitiesEl.Children.Count} entities, {s.Room.TriggersEl.Children.Count} triggers)";
        else
        {
            var el = s.El!;
            _selLabel.Text = $"{s.Kind}: {(s.Decal ? el.Str("texture") : el.Name)}  ({s.Room.Name})";
            int nodes = MapCanvas.Nodes(el).Count();
            if (s.IsNode) _selLabel.Text += $"  · node {s.Node + 1}/{nodes}";
            else if (nodes > 0) _selLabel.Text += $"  · {nodes} node(s)";
            int more = SelectedItemsOrPrimary().Count - 1;
            if (more > 0) _selLabel.Text += $"  +{more} more (edits apply to all {el.Name})";
        }
        _attrs.AllowUserToAddRows = true;
        _loadingGrid = false;
    }

    private static string FormatValue(object? v) => v switch
    {
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
    };

    private void CommitGridRow(int row)
    {
        if (_loadingGrid || _canvas.Selected is not { } s || row < 0 || row >= _attrs.Rows.Count) return;
        var r = _attrs.Rows[row];
        if (r.IsNewRow) return;
        string? key = (r.Cells[0].Value as string)?.Trim();
        if (string.IsNullOrEmpty(key)) return;

        var primary = s.El ?? s.Room.El;
        var existing = primary.Find(key);
        object raw = r.Cells[2].Value ?? "";
        object value = raw switch
        {
            bool b => b,
            string label when r.Cells[2] is DataGridViewComboBoxCell && r.Tag is FieldInfo fi && fi.Options.Any(o => o.Key == label)
                => fi.Options.First(o => o.Key == label).Value,
            _ => MapBin.ParseText(Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "", existing?.Type)
        };
        var targets = GridTargets(s);
        bool changed = targets.Any(t =>
            (t.El ?? t.Room.El).Find(key) is not { } a || !Equals(a.Value, MapBin.Coerce(value, MapBin.TypeFor(value, a.Type))));
        if (!changed) return;

        PushUndo();
        if (s.El == null && key is "width" or "height" && value is IConvertible)
        {
            // resizing keeps tiles, entities and decals where they are
            int v = Math.Max(8, MapModel.Snap8(Convert.ToSingle(value, CultureInfo.InvariantCulture)));
            var b = s.Room.Bounds;
            MapModel.ResizeRoom(s.Room, key == "width" ? new Rectangle(b.X, b.Y, v, b.Height) : new Rectangle(b.X, b.Y, b.Width, v));
        }
        else foreach (var t in targets) (t.El ?? t.Room.El).Set(key, value);
        if (s.El == null) RefreshRoomList();
        foreach (var rr in targets.Where(t => t.Decal).Select(t => t.Room).Distinct()) rr.InvalidateRender();
        MarkDirty();
        BeginInvoke(LoadGrid);
        _canvas.Invalidate();
    }

    // ---- stylegrounds & metadata ----

    /// <summary>Tree editor for everything in the map outside "levels": Style (fg/bg stylegrounds), Filler, meta.</summary>
    private void EditMapMetadata()
    {
        if (_map == null) { Log("Open a map first."); return; }
        var root = new BinElement("(map)");
        foreach (var c in _map.Root.Children.Where(c => c.Name != "levels")) root.Children.Add(c.Clone());

        using var f = new Form { Text = "Stylegrounds & metadata", Size = new Size(900, 600), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false };
        var tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        grid.Columns.Add("Key", "Attribute");
        grid.Columns.Add("Type", "Type");
        grid.Columns[1].ReadOnly = true;
        grid.Columns.Add("Value", "Value");
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.LeftToRight };
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 330 };
        split.Panel1.Controls.Add(tree);
        split.Panel2.Controls.Add(grid);
        f.Controls.Add(split);
        f.Controls.Add(buttons);

        static string NodeText(BinElement e)
        {
            string tag = e.Str("texture") is { Length: > 0 } t ? t : e.Str("only");
            return tag.Length > 0 ? $"{e.Name}  ({tag})" : e.Name;
        }
        void Fill(TreeNodeCollection nodes, BinElement el)
        {
            foreach (var c in el.Children)
            {
                if (c.Name == "levels") continue;
                var n = new TreeNode(NodeText(c)) { Tag = c };
                Fill(n.Nodes, c);
                nodes.Add(n);
            }
        }
        void Rebuild(BinElement? select)
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();
            Fill(tree.Nodes, root);
            tree.ExpandAll();
            tree.EndUpdate();
            var node = AllNodes(tree.Nodes).FirstOrDefault(n => n.Tag == select) ?? (tree.Nodes.Count > 0 ? tree.Nodes[0] : null);
            tree.SelectedNode = node;
            node?.EnsureVisible();
        }
        BinElement Selected() => tree.SelectedNode?.Tag as BinElement ?? root;
        BinElement ParentOf(BinElement e) => tree.SelectedNode?.Parent?.Tag as BinElement ?? root;

        bool loading = false;
        void LoadAttrs()
        {
            loading = true;
            grid.Rows.Clear();
            if (tree.SelectedNode?.Tag is BinElement e)
                foreach (var a in e.Attrs) grid.Rows.Add(a.Key, a.Type.ToString(), FormatValue(a.Value));
            grid.AllowUserToAddRows = tree.SelectedNode != null;
            loading = false;
        }
        tree.AfterSelect += (_, _) => LoadAttrs();
        grid.CellEndEdit += (_, e) =>
        {
            if (loading || tree.SelectedNode?.Tag is not BinElement el || e.RowIndex < 0) return;
            var cells = grid.Rows[e.RowIndex].Cells;
            if ((cells[0].Value as string)?.Trim() is not { Length: > 0 } key) return;
            el.Set(key, MapBin.ParseText(cells[2].Value as string ?? "", el.Find(key)?.Type));
            tree.SelectedNode.Text = NodeText(el);
            f.BeginInvoke(LoadAttrs);
        };
        grid.UserDeletingRow += (_, e) =>
        {
            if (tree.SelectedNode?.Tag is BinElement el && e.Row?.Cells[0].Value is string key) el.Remove(key);
        };

        void Btn(string text, Action a)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (_, _) => a();
            buttons.Controls.Add(b);
        }
        Btn("Add child…", () =>
        {
            var parent = Selected();
            string hint = parent.Name switch { "Style" => "Backgrounds", "Backgrounds" or "Foregrounds" => "parallax", "apply" => "parallax", _ => "meta" };
            if (Prompt($"Element name to add under {parent.Name} (parallax, apply, snowFg, Backgrounds…)", hint) is not { Length: > 0 } name) return;
            var el = new BinElement(name.Trim());
            if (name.Trim() == "parallax") { el.Set("texture", "bgs/04/bg0"); el.Set("only", "*"); el.Set("scrollx", 0.5f); el.Set("scrolly", 0.5f); }
            parent.Children.Add(el);
            Rebuild(el);
        });
        Btn("Duplicate", () =>
        {
            if (tree.SelectedNode?.Tag is not BinElement el) return;
            var list = ParentOf(el).Children;
            var copy = el.Clone();
            list.Insert(list.IndexOf(el) + 1, copy);
            Rebuild(copy);
        });
        Btn("Remove", () =>
        {
            if (tree.SelectedNode?.Tag is not BinElement el) return;
            ParentOf(el).Children.Remove(el);
            Rebuild(ParentOf(el));
        });
        void Shift(int d)
        {
            if (tree.SelectedNode?.Tag is not BinElement el) return;
            var list = ParentOf(el).Children;
            int i = list.IndexOf(el), j = i + d;
            if (i < 0 || j < 0 || j >= list.Count) return;
            (list[i], list[j]) = (list[j], list[i]);
            Rebuild(el);
        }
        Btn("Move up", () => Shift(-1));
        Btn("Move down", () => Shift(1));
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        f.AcceptButton = null;
        f.CancelButton = cancel;

        Rebuild(null);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        grid.EndEdit();

        PushUndo();
        var children = _map.Root.Children;
        int levelsAt = children.FindIndex(c => c.Name == "levels");
        var levels = levelsAt >= 0 ? children[levelsAt] : null;
        children.Clear();
        children.AddRange(root.Children);
        if (levels != null) children.Insert(Math.Min(levelsAt, children.Count), levels);
        MarkDirty();
        Log("Updated stylegrounds & metadata.");
    }
}

/// <summary>Map items on the system clipboard as JSON (positions in world space), so they paste across maps and instances.</summary>
public static class ItemClipboard
{
    public const string Format = "CELMapViewer.items";

    public static string LayerOf(Selection s) => s.Decal ? (s.FgDecal ? "fgdecal" : "bgdecal") : s.Trigger ? "trigger" : "entity";

    public static string Serialize(IEnumerable<(string Layer, BinElement El)> items)
    {
        var arr = new JsonArray();
        foreach (var (layer, el) in items) arr.Add(new JsonObject { ["layer"] = layer, ["el"] = ToJson(el) });
        return new JsonObject { ["format"] = Format, ["items"] = arr }.ToJsonString();
    }

    public static List<(string Layer, BinElement El)>? Deserialize(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains(Format)) return null;
        try
        {
            var root = JsonNode.Parse(text) as JsonObject;
            if (root?["format"]?.GetValue<string>() != Format || root["items"] is not JsonArray arr) return null;
            var list = new List<(string, BinElement)>();
            foreach (var item in arr.OfType<JsonObject>())
                if (item["layer"]?.GetValue<string>() is { } layer && item["el"] is JsonObject el)
                    list.Add((layer, FromJson(el)));
            return list;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static JsonObject ToJson(BinElement el)
    {
        var attrs = new JsonArray();
        foreach (var a in el.Attrs)
        {
            JsonNode? v = a.Value switch
            {
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                float f => JsonValue.Create(f),
                _ => JsonValue.Create(Convert.ToString(a.Value, CultureInfo.InvariantCulture))
            };
            attrs.Add(new JsonArray(JsonValue.Create(a.Key), JsonValue.Create((int)a.Type), v));
        }
        var children = new JsonArray();
        foreach (var c in el.Children) children.Add(ToJson(c));
        return new JsonObject { ["name"] = el.Name, ["attrs"] = attrs, ["children"] = children };
    }

    private static BinElement FromJson(JsonObject o)
    {
        var el = new BinElement(o["name"]?.GetValue<string>() ?? "");
        foreach (var a in (o["attrs"] as JsonArray ?? new()).OfType<JsonArray>())
        {
            string key = a[0]!.GetValue<string>();
            var type = (BinType)a[1]!.GetValue<int>();
            var v = a[2]!;
            object value = type switch
            {
                BinType.Bool => v.GetValue<bool>(),
                BinType.Byte or BinType.Short or BinType.Int => v.GetValue<int>(),
                BinType.Float => v.GetValue<float>(),
                _ => v.GetValue<string>()
            };
            el.Attrs.Add(new BinAttr(key, type, value));
        }
        foreach (var c in (o["children"] as JsonArray ?? new()).OfType<JsonObject>()) el.Children.Add(FromJson(c));
        return el;
    }
}
