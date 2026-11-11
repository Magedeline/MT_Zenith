using System.Globalization;

namespace CELMapViewer;

public sealed partial class MainForm : Form
{
    private MapBin? _map;
    private string? _path;
    private bool _dirty;
    private readonly List<BinElement> _undo = new(), _redo = new();
    private const int UndoLimit = 30;

    private readonly GameLink _game = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 250 };
    private bool _polling;
    private GameState? _lastState;
    private string? _lastGameRoom;

    private readonly MapCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "Filter rooms…" };
    private readonly TreeView _roomTree = new() { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true, ShowLines = false };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _paletteSearch = new() { Dock = DockStyle.Top, PlaceholderText = "Search entities/triggers…" };
    private readonly ComboBox _paletteKind = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListBox _palette = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _paletteInfo = new() { Dock = DockStyle.Bottom, Height = 34, ForeColor = Color.DimGray };
    private readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly Dictionary<Tool, ToolStripButton> _toolButtons = new();
    private readonly ToolStripComboBox _layerBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 60 };
    private readonly ToolStripComboBox _editLayerBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 130 };
    private readonly ToolStripComboBox _tilesetBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 220 };
    private readonly ToolStripStatusLabel _zoomStatus = new() { Text = "50%" };
    private readonly ToolStripMenuItem _recentMenu = new("Recent &files");
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly System.Windows.Forms.Timer _paletteDebounce = new() { Interval = 300 };
    private readonly Label _selLabel = new() { Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    private readonly DataGridView _attrs = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.CellSelect
    };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8.5f) };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _gameStatus = new() { Text = "game: ?" };
    private readonly ToolStripStatusLabel _cursorStatus = new();

    private ToolStripMenuItem _follow = null!, _reloadOnSave = null!;
    private bool _loadingGrid;

    public MainForm(string? map, string? room)
    {
        Text = AppInfo.Title;
        Size = new Size(1400, 900);
        KeyPreview = true;
        Icon = AppInfo.LoadIcon() ?? SystemIcons.Application;

        BuildMenu();
        BuildLayout();
        WireEvents();
        InitAi();

        _poll.Tick += async (_, _) => await PollGameAsync();
        _poll.Start();

        Shown += (_, _) =>
        {
            if (map != null && File.Exists(map))
            {
                Open(map);
                if (room != null && FindRoom(room) is { } r) { _canvas.FocusRoom(r); SelectRoomInList(r); }
            }
            else Log("Open a map (Ctrl+O), or enter a level in game with Game ▸ Follow game on.");
        };
    }

    // ---- UI construction ----

    private void BuildMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Open…", null, (_, _) => OpenDialog(), Keys.Control | Keys.O));
        file.DropDownItems.Add(new ToolStripMenuItem("&Save", null, async (_, _) => await SaveAsync(_path), Keys.Control | Keys.S));
        file.DropDownItems.Add(new ToolStripMenuItem("Save &As…", null, async (_, _) => await SaveAsAsync(), Keys.Control | Keys.Shift | Keys.S));
        file.DropDownItems.Add(new ToolStripMenuItem("Re&name map file…", null, async (_, _) => await RenameMapAsync()));
        file.DropDownItems.Add(new ToolStripMenuItem("&Revert", null, (_, _) => { if (_path != null && ConfirmDiscard()) Open(_path); }));
        file.DropDownItems.Add(_recentMenu);
        file.DropDownOpening += (_, _) => RefreshRecentMenu();
        file.DropDownItems.Add(new ToolStripSeparator());
        var shot = new ToolStripMenuItem("Screens&hot");
        shot.DropDownItems.Add(new ToolStripMenuItem("&Entire map…", null, async (_, _) => await ScreenshotAsync(ShotKind.Map), Keys.Control | Keys.Shift | Keys.P));
        shot.DropDownItems.Add(new ToolStripMenuItem("Selected &room…", null, async (_, _) => await ScreenshotAsync(ShotKind.Room), Keys.Control | Keys.Alt | Keys.P));
        shot.DropDownItems.Add(new ToolStripMenuItem("Current &view…", null, async (_, _) => await ScreenshotAsync(ShotKind.View)));
        file.DropDownItems.Add(shot);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("Set &Celeste folder…", null, (_, _) => ChooseCelesteDir()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(new ToolStripMenuItem("&Undo", null, (_, _) => Undo(), Keys.Control | Keys.Z));
        edit.DropDownItems.Add(new ToolStripMenuItem("&Redo", null, (_, _) => Redo(), Keys.Control | Keys.Y));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(new ToolStripMenuItem("Cu&t", null, (_, _) => CutSelected()) { ShortcutKeyDisplayString = "Ctrl+X" });
        edit.DropDownItems.Add(new ToolStripMenuItem("&Copy", null, (_, _) => CopySelected()) { ShortcutKeyDisplayString = "Ctrl+C" });
        edit.DropDownItems.Add(new ToolStripMenuItem("&Paste", null, (_, _) => Paste()) { ShortcutKeyDisplayString = "Ctrl+V" });
        edit.DropDownItems.Add(new ToolStripMenuItem("&Duplicate", null, (_, _) => Duplicate(), Keys.Control | Keys.D));
        edit.DropDownItems.Add(new ToolStripMenuItem("De&lete", null, (_, _) => DeleteSelected()) { ShortcutKeyDisplayString = "Del" });
        edit.DropDownItems.Add(new ToolStripMenuItem("Select &all in room", null, (_, _) => SelectAllInRoom()) { ShortcutKeyDisplayString = "Ctrl+A" });
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(new ToolStripMenuItem("Flip &horizontally", null, (_, _) => FlipSelected(true)) { ShortcutKeyDisplayString = "H" });
        edit.DropDownItems.Add(new ToolStripMenuItem("Flip &vertically", null, (_, _) => FlipSelected(false)) { ShortcutKeyDisplayString = "Shift+H" });
        edit.DropDownItems.Add(new ToolStripMenuItem("Add &node", null, (_, _) => AddNode()) { ShortcutKeyDisplayString = "N" });

        var roomMenu = new ToolStripMenuItem("&Room");
        roomMenu.DropDownItems.Add(new ToolStripMenuItem("&New room…", null, (_, _) => NewRoom(null), Keys.Control | Keys.N));
        roomMenu.DropDownItems.Add(new ToolStripMenuItem("&Rename room…", null, (_, _) => RenameRoom(SelectedRoom()), Keys.F2));
        roomMenu.DropDownItems.Add(new ToolStripMenuItem("&Duplicate room", null, (_, _) => DuplicateRoom(SelectedRoom()), Keys.Control | Keys.Shift | Keys.D));
        roomMenu.DropDownItems.Add(new ToolStripMenuItem("De&lete room…", null, (_, _) => DeleteRoom(SelectedRoom()), Keys.Control | Keys.Shift | Keys.Delete));
        roomMenu.DropDownItems.Add(new ToolStripSeparator());
        roomMenu.DropDownItems.Add(new ToolStripMenuItem("Room &properties", null, (_, _) => { if (SelectedRoom() is { } r) _canvas.Select(Selection.OfRoom(r)); }));
        roomMenu.DropDownItems.Add(new ToolStripSeparator());
        roomMenu.DropDownItems.Add(new ToolStripMenuItem("&Stylegrounds && metadata…", null, (_, _) => EditMapMetadata()));

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(new ToolStripMenuItem("&Fit map", null, (_, _) => _canvas.FitAll(), Keys.Control | Keys.D0));
        view.DropDownItems.Add(Toggle("&Background tiles", true, v => _canvas.ShowBg = v));
        view.DropDownItems.Add(Toggle("&Entities", true, v => _canvas.ShowEntities = v));
        view.DropDownItems.Add(Toggle("&Triggers", true, v => _canvas.ShowTriggers = v));
        view.DropDownItems.Add(Toggle("F&G decals", true, v => _canvas.ShowFgDecals = v));
        view.DropDownItems.Add(Toggle("B&G decals", true, v => _canvas.ShowBgDecals = v));
        view.DropDownItems.Add(Toggle("&Labels", true, v => _canvas.ShowLabels = v));
        view.DropDownItems.Add(Toggle("&Minimap", _settings.ShowMinimap, v => { _canvas.ShowMinimap = v; _settings.ShowMinimap = v; _settings.Save(); }));
        _canvas.ShowMinimap = _settings.ShowMinimap;
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(new ToolStripMenuItem("Zoom &in", null, (_, _) => _canvas.SetZoom(_canvas.Zoom * 1.25f), Keys.Control | Keys.Oemplus));
        view.DropDownItems.Add(new ToolStripMenuItem("Zoom &out", null, (_, _) => _canvas.SetZoom(_canvas.Zoom * 0.8f), Keys.Control | Keys.OemMinus));
        view.DropDownItems.Add(new ToolStripMenuItem("Zoom &100%", null, (_, _) => _canvas.SetZoom(1f), Keys.Control | Keys.D1));
        view.DropDownItems.Add(new ToolStripMenuItem("Re&load tilesets && sprites", null, (_, _) => ReloadAssets()));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(BuildThemeMenu());

        var game = new ToolStripMenuItem("&Game");
        _follow = Toggle("&Follow game (open its map, show room and player)", true, _ => { });
        _reloadOnSave = Toggle("&Reload in game after save", true, _ => { });
        game.DropDownItems.Add(_follow);
        game.DropDownItems.Add(_reloadOnSave);
        game.DropDownItems.Add(new ToolStripSeparator());
        game.DropDownItems.Add(new ToolStripMenuItem("&Teleport to selected room", null, async (_, _) => await TeleportAsync(SelectedRoom(), null, false), Keys.F5));
        game.DropDownItems.Add(new ToolStripMenuItem("Reload &map in game", null, async (_, _) => await ReloadGameAsync(), Keys.F6));
        game.DropDownItems.Add(new ToolStripMenuItem("Go to &game's room", null, (_, _) => GoToGameRoom(), Keys.F7));
        game.DropDownItems.Add(new ToolStripSeparator());
        game.DropDownItems.Add(new ToolStripMenuItem("&Launch Celeste", null, (_, _) => Log(GameLink.LaunchGame())));

        var pcg = new ToolStripMenuItem("&PCG");
        pcg.DropDownItems.Add(new ToolStripMenuItem("&Generate rooms (PCGHelper seleCte)…", null, (_, _) => GenerateRooms(), Keys.Control | Keys.G));
        pcg.DropDownItems.Add(new ToolStripMenuItem("&Locate PCGHelper…", null, (_, _) => LocatePcgHelper(force: true)));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, roomMenu, view, game, pcg, BuildAiMenu() });
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private ToolStripMenuItem BuildThemeMenu()
    {
        var menu = new ToolStripMenuItem("T&heme");
        var items = new List<(string Id, ToolStripMenuItem Item)>();
        void Add(string id, string text)
        {
            var item = new ToolStripMenuItem(text, null, (_, _) =>
            {
                Themes.Select(id);
                _settings.Theme = Themes.Selected;
                _settings.Save();
                foreach (var (i, it) in items) it.Checked = i == Themes.Selected;
            }) { Checked = id == Themes.Selected };
            items.Add((id, item));
            menu.DropDownItems.Add(item);
        }
        Add(Themes.SystemId, "&System (follow Windows light/dark)");
        string? group = null;
        foreach (var t in Themes.All)
        {
            if (t.Group != group)
            {
                menu.DropDownItems.Add(new ToolStripSeparator());
                menu.DropDownItems.Add(new ToolStripLabel(t.Group) { Font = new Font(menu.Font, FontStyle.Bold) });
                group = t.Group;
            }
            Add(t.Id, t.Name + (t.Dark ? "" : "  (light)"));
        }
        return menu;
    }

    private ToolStripMenuItem Toggle(string text, bool initial, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = initial };
        item.CheckedChanged += (_, _) => { set(item.Checked); _canvas.Invalidate(); };
        return item;
    }

    private void BuildLayout()
    {
        _attrs.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", HeaderText = "Attribute", FillWeight = 40 });
        _attrs.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Type", FillWeight = 20, ReadOnly = true });
        _attrs.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", FillWeight = 60 });

        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_roomTree);
        left.Controls.Add(_filter);

        var propsPage = new TabPage("Properties");
        propsPage.Controls.Add(_attrs);
        propsPage.Controls.Add(_selLabel);
        var palettePage = new TabPage("Palette");
        _paletteKind.Items.AddRange(new object[] { "All", "Entities", "Triggers" });
        _paletteKind.SelectedIndex = 0;
        palettePage.Controls.Add(_palette);
        palettePage.Controls.Add(_paletteInfo);
        palettePage.Controls.Add(_paletteKind);
        palettePage.Controls.Add(_paletteSearch);
        _tabs.TabPages.Add(propsPage);
        _tabs.TabPages.Add(palettePage);
        _tabs.TabPages.Add(BuildDecalTab());
        _tabs.TabPages.Add(BuildAiTab());

        var rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        rightSplit.Panel1.Controls.Add(_tabs);
        rightSplit.Panel2.Controls.Add(_log);

        var inner = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
        inner.Panel1.Controls.Add(_canvas);
        inner.Panel2.Controls.Add(rightSplit);

        var outer = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        outer.Panel1.Controls.Add(left);
        outer.Panel2.Controls.Add(inner);

        var statusStrip = new StatusStrip();
        statusStrip.Items.AddRange(new ToolStripItem[] { _status, _cursorStatus, _zoomStatus, _gameStatus });

        BuildToolbar();
        Controls.Add(outer);
        Controls.Add(_toolbar);
        Controls.Add(statusStrip);
        outer.BringToFront();
        MainMenuStrip?.SendToBack();

        Load += (_, _) =>
        {
            outer.SplitterDistance = 220;
            inner.SplitterDistance = Math.Max(200, inner.Width - 340);
            rightSplit.SplitterDistance = (int)(rightSplit.Height * 0.6);
        };
    }

    private void BuildToolbar()
    {
        void AddTool(Tool tool, string text, string tip)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip, CheckOnClick = false, DisplayStyle = ToolStripItemDisplayStyle.Text };
            b.Click += (_, _) => SetTool(tool);
            _toolButtons[tool] = b;
            _toolbar.Items.Add(b);
        }
        AddTool(Tool.Select, "Select", "Select / move entities and triggers, pan (V)");
        AddTool(Tool.Room, "Room", "Move rooms, resize by their edges, drag on empty space to create one (R)");
        AddTool(Tool.Brush, "Brush", "Paint tiles; right button erases, Ctrl+click picks (B)");
        AddTool(Tool.Rect, "Rect", "Fill a rectangle of tiles; right button erases (T)");
        AddTool(Tool.Fill, "Fill", "Flood fill tiles (F)");
        AddTool(Tool.Line, "Line", "Draw a line of tiles; right button erases (L)");
        AddTool(Tool.Place, "Place", "Place the palette's selected entity/trigger; drag to size it; Alt = no snap (P)");
        AddTool(Tool.Decal, "Decal", "Place the Decals tab's selected decal on the FG/BG layer (D)");
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripLabel("Select"));
        _editLayerBox.Items.AddRange(new object[] { "Entities + triggers", "Entities", "Triggers", "FG decals", "BG decals" });
        _editLayerBox.SelectedIndex = 0;
        _editLayerBox.SelectedIndexChanged += (_, _) => { _canvas.ActiveLayer = (EditLayer)_editLayerBox.SelectedIndex; _canvas.Select(null); };
        _toolbar.Items.Add(_editLayerBox);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripLabel("Layer"));
        _layerBox.Items.AddRange(new object[] { "FG", "BG" });
        _layerBox.SelectedIndex = 0;
        _layerBox.SelectedIndexChanged += (_, _) =>
        {
            _canvas.PaintFg = _layerBox.SelectedIndex == 0;
            RefreshTilesets();
            if (_canvas.Tool == Tool.Decal) SetTool(Tool.Decal);
        };
        _toolbar.Items.Add(_layerBox);
        _toolbar.Items.Add(new ToolStripLabel("Tileset"));
        _tilesetBox.SelectedIndexChanged += (_, _) =>
        {
            if (_tilesetBox.SelectedItem is Autotiler.Tileset t) _canvas.PaintChar = t.Id;
            if (_canvas.Tool is not (Tool.Brush or Tool.Rect or Tool.Fill or Tool.Line)) SetTool(Tool.Brush);
        };
        _toolbar.Items.Add(_tilesetBox);
        SetTool(Tool.Select);
    }

    private void SetTool(Tool tool)
    {
        _canvas.Tool = tool;
        foreach (var (t, b) in _toolButtons) b.Checked = t == tool;
        if (tool == Tool.Place) _tabs.SelectedIndex = 1;
        if (tool == Tool.Decal)
        {
            _tabs.SelectedIndex = 2;
            _editLayerBox.SelectedIndex = (int)(_canvas.PaintFg ? EditLayer.FgDecals : EditLayer.BgDecals);
        }
        _status.Text = tool switch
        {
            Tool.Place => _canvas.PlaceItem == null ? "Place: pick an entity or trigger in the Palette tab" : "Place: " + _canvas.PlaceItem.Display,
            Tool.Decal => _canvas.PlaceDecal == null ? "Decal: pick a decal in the Decals tab" : $"Decal: {_canvas.PlaceDecal} on {(_canvas.PaintFg ? "FG" : "BG")} (Layer box)",
            Tool.Brush or Tool.Rect or Tool.Fill or Tool.Line => $"{tool}: {(_canvas.PaintFg ? "FG" : "BG")} '{_canvas.PaintChar}'  (right button erases, Ctrl+click picks)",
            Tool.Room => "Room: drag to move, drag an edge to resize, drag on empty space to create",
            _ => "Select"
        };
    }

    private void RefreshTilesets()
    {
        var tiler = _canvas.PaintFg ? _canvas.FgTiler : _canvas.BgTiler;
        _tilesetBox.Items.Clear();
        if (tiler == null) return;
        foreach (var t in tiler.Tilesets.OrderBy(t => t.Id)) _tilesetBox.Items.Add(t);
        SelectTilesetItem(_canvas.PaintChar);
    }

    private void SelectTilesetItem(char c)
    {
        foreach (var item in _tilesetBox.Items)
            if (item is Autotiler.Tileset t && t.Id == c) { _tilesetBox.SelectedItem = item; return; }
        if (_tilesetBox.Items.Count > 0 && _tilesetBox.SelectedIndex < 0)
        {
            var tool = _canvas.Tool;
            _tilesetBox.SelectedIndex = 0;
            SetTool(tool);
        }
    }

    private void WireEvents()
    {
        _filter.TextChanged += (_, _) => RefreshRoomList();
        _roomTree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is Room r && !_suppressListFocus) { _canvas.FocusRoom(r); _canvas.Select(Selection.OfRoom(r)); }
        };
        _roomTree.NodeMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.Node.Tag is not Room r) return;
            _roomTree.SelectedNode = e.Node;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Rename room…", null, (_, _) => RenameRoom(r));
            menu.Items.Add("Teleport here", null, async (_, _) => await TeleportAsync(r, null, false));
            menu.Items.Add("Duplicate room", null, (_, _) => DuplicateRoom(r));
            menu.Items.Add("Delete room…", null, (_, _) => DeleteRoom(r));
            menu.Items.Add("Screenshot room…", null, async (_, _) => await ScreenshotAsync(ShotKind.Room, r));
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(_roomTree, e.Location);
        };
        _roomTree.NodeMouseDoubleClick += async (_, e) => { if (e.Node.Tag is Room r) await TeleportAsync(r, null, false); };

        _palette.SelectedIndexChanged += (_, _) =>
        {
            _canvas.PlaceItem = _palette.SelectedItem as PlacementDef;
            if (_canvas.PlaceItem is { } p)
            {
                _paletteInfo.Text = $"{(p.Plugin.Trigger ? "Trigger" : "Entity")} from {p.Plugin.Source}" +
                                    (p.Plugin.MinNodes > 0 ? $", {p.Plugin.MinNodes} node(s)" : "") + ". Click in a room to place.";
                SetTool(Tool.Place);
            }
        };
        _paletteSearch.TextChanged += (_, _) => RefreshPalette();
        _paletteKind.SelectedIndexChanged += (_, _) => RefreshPalette();
        _paletteDebounce.Tick += (_, _) => { _paletteDebounce.Stop(); RefreshPalette(); _canvas.Invalidate(); };
        PluginCatalog.Changed += OnCatalogChanged;
        FormClosed += (_, _) => PluginCatalog.Changed -= OnCatalogChanged;

        _canvas.SelectionChanged += () => { LoadGrid(); if (_canvas.Selected is { } s) SelectRoomInList(s.Room); };
        _canvas.BeforeEdit += PushUndo;
        _canvas.Edited += () => { MarkDirty(); LoadGrid(); };
        _canvas.RoomsChanged += () => { RefreshRoomList(); LoadGrid(); };
        _canvas.CreateRoomRequested += rect => NewRoom(rect);
        _canvas.ViewChanged += () => _zoomStatus.Text = $"{_canvas.Zoom * 100:0}%";
        _canvas.TileCharPicked += c => { SelectTilesetItem(c); SetTool(_canvas.Tool); };
        _canvas.RoomActivated += async r => await TeleportAsync(r, LocalOf(r, _canvas.PointToClient(Cursor.Position)), false);
        _canvas.ContextRequested += ShowContextMenu;
        _canvas.MouseMove += (_, e) =>
        {
            var w = _canvas.ToWorld(e.Location);
            var r = _canvas.RoomAt(w);
            _cursorStatus.Text = r == null ? $"{w.X:0}, {w.Y:0}" : $"{r.Name}  {w.X - r.X:0}, {w.Y - r.Y:0}";
        };

        WireGrid();

        KeyDown += (_, e) =>
        {
            if (TextFocused() || e.Alt) return;
            bool mapFocus = _canvas.Focused || _roomTree.Focused;
            if (e.Control)
            {
                if (!mapFocus || e.Shift) return;
                Action? act = e.KeyCode switch
                {
                    Keys.C => CopySelected, Keys.X => CutSelected, Keys.V => Paste, Keys.A => SelectAllInRoom,
                    Keys.F => () => _filter.Focus(), _ => null
                };
                if (act == null) return;
                act();
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Delete && _canvas.Selected != null) { DeleteSelected(); e.Handled = true; }
            else if (_canvas.Focused && !e.Shift && e.KeyCode switch
                     {
                         Keys.V => Tool.Select, Keys.R => Tool.Room, Keys.B => Tool.Brush, Keys.T => Tool.Rect,
                         Keys.F => Tool.Fill, Keys.L => Tool.Line, Keys.P => Tool.Place, Keys.D => Tool.Decal, _ => (Tool?)null
                     } is Tool tool)
            {
                SetTool(tool);
                e.Handled = true;
            }
            else if (_canvas.Focused && e.KeyCode == Keys.H) { FlipSelected(!e.Shift); e.Handled = true; }
            else if (_canvas.Focused && e.KeyCode == Keys.N && !e.Shift) { AddNode(); e.Handled = true; }
            else if (_canvas.Focused && e.KeyCode == Keys.Escape) { _canvas.Select(null); e.Handled = true; }
            else if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down && _canvas.Selected != null && _canvas.Focused)
            {
                int step = e.Shift ? 1 : 8;
                int dx = e.KeyCode == Keys.Left ? -step : e.KeyCode == Keys.Right ? step : 0;
                int dy = e.KeyCode == Keys.Up ? -step : e.KeyCode == Keys.Down ? step : 0;
                Nudge(dx, dy);
                e.Handled = true;
            }
        };

        FormClosing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };
    }

    // ---- file ----

    private void OpenDialog()
    {
        if (!ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog { Filter = "Celeste map (*.bin)|*.bin", InitialDirectory = DefaultMapsDir() };
        if (dlg.ShowDialog(this) == DialogResult.OK) Open(dlg.FileName);
    }

    private static string DefaultMapsDir()
    {
        string? mod = GameLink.FindModDir();
        string dir = mod != null ? Path.Combine(mod, "Maps", "CEL") : "";
        return Directory.Exists(dir) ? dir : Environment.CurrentDirectory;
    }

    public void Open(string path)
    {
        try
        {
            OpenCore(path);
        }
        catch (Exception ex)
        {
            Log("Open failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenCore(string path)
    {
        _map = MapBin.Load(path);
        _path = Path.GetFullPath(path);
        _undo.Clear(); _redo.Clear();
        _dirty = false;
        LoadTilers();
        _canvas.NextId = () => _map == null ? 0 : MapModel.NextId(_map);
        RebuildRooms(keepView: false);
        UpdateTitle();
        _settings.AddRecent(_path);
        PluginCatalog.StartLoading();
        Log($"Opened {_path} ({_canvas.Rooms.Count} rooms; tilesets: {_canvas.FgTiler?.Source ?? "none"})");
    }

    private void LoadTilers()
    {
        if (_map == null || _path == null) return;
        string? mod = MapModel.ModDirOf(_path);
        _canvas.FgTiler = Autotiler.For(MapModel.TilesetXml(_map, true), true, mod);
        _canvas.BgTiler = Autotiler.For(MapModel.TilesetXml(_map, false), false, mod);
        RefreshTilesets();
    }

    private void ReloadAssets()
    {
        Autotiler.ClearCache();
        EntityVisuals.ClearCache();
        LoadTilers();
        Log("Reloaded tilesets and sprites.");
    }

    private void RefreshRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        foreach (string p in _settings.RecentFiles)
        {
            string path = p;
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem(path, null, (_, _) =>
            {
                if (!File.Exists(path)) { Log("Missing: " + path); return; }
                if (ConfirmDiscard()) Open(path);
            }) { Enabled = File.Exists(path) });
        }
        _recentMenu.Enabled = _recentMenu.DropDownItems.Count > 0;
    }

    private void OnCatalogChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { _paletteDebounce.Stop(); _paletteDebounce.Start(); }); } catch { }
    }

    private void RefreshPalette()
    {
        string q = _paletteSearch.Text.Trim();
        int kind = _paletteKind.SelectedIndex;
        var selected = _palette.SelectedItem as PlacementDef;
        var items = PluginCatalog.AllPlacements()
            .Where(p => kind == 0 || (kind == 2) == p.Plugin.Trigger)
            .Where(p => q.Length == 0 || p.Display.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        p.Plugin.Source.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(3000).ToArray();
        _palette.BeginUpdate();
        _palette.Items.Clear();
        _palette.Items.AddRange(items);
        if (selected != null)
        {
            int i = Array.FindIndex(items, p => p.Plugin.Name == selected.Plugin.Name && p.Name == selected.Name);
            if (i >= 0) _palette.SelectedIndex = i;
        }
        _palette.EndUpdate();
        if (_palette.SelectedItem == null)
            _paletteInfo.Text = $"{items.Length} placements{(PluginCatalog.Loading ? " (loading plugins…)" : "")}";
    }

    private async Task SaveAsAsync()
    {
        if (_map == null) return;
        using var dlg = new SaveFileDialog { Filter = "Celeste map (*.bin)|*.bin", InitialDirectory = Path.GetDirectoryName(_path) ?? DefaultMapsDir(), FileName = Path.GetFileName(_path) };
        if (dlg.ShowDialog(this) == DialogResult.OK) await SaveAsync(dlg.FileName);
    }

    private async Task SaveAsync(string? path)
    {
        if (_map == null) return;
        if (path == null) { await SaveAsAsync(); return; }
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            _map.Save(path);
            _path = Path.GetFullPath(path);
            _dirty = false;
            UpdateTitle();
            Log($"Saved {_path} (backup: .bin.bak)");
        }
        catch (Exception ex)
        {
            Log("Save failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (_reloadOnSave.Checked && _lastState != null) await ReloadGameAsync();
    }

    /// <summary>Renames the open .bin on disk (and its .bak); the map's package name follows when it matched the file name.</summary>
    private async Task RenameMapAsync()
    {
        if (_map == null) { Log("Open a map first."); return; }
        if (_path == null) { await SaveAsAsync(); return; }
        string oldName = Path.GetFileNameWithoutExtension(_path);
        string? input = Prompt("New map file name (without .bin). The in-game map ID changes with it, so saves and dialog keys using the old name won't match.", oldName);
        if (input == null) return;
        string name = input.Trim();
        if (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || name == oldName) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "That name has characters a file name can't contain.", AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string oldPath = _path, newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, name + ".bin");
        bool caseOnly = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && File.Exists(newPath))
        {
            MessageBox.Show(this, $"{Path.GetFileName(newPath)} already exists in that folder.", AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            if (File.Exists(oldPath))
            {
                if (caseOnly) { string tmp = oldPath + ".renaming"; File.Move(oldPath, tmp); File.Move(tmp, newPath); }
                else File.Move(oldPath, newPath);
            }
            if (!caseOnly && File.Exists(oldPath + ".bak") && !File.Exists(newPath + ".bak")) File.Move(oldPath + ".bak", newPath + ".bak");
        }
        catch (Exception ex)
        {
            Log("Rename failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Rename failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _path = Path.GetFullPath(newPath);
        _settings.RecentFiles.RemoveAll(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
        _settings.AddRecent(_path);
        RefreshRecentMenu();
        if (_map.Package == oldName)
        {
            _map.Package = name;
            if (!_dirty)
            {
                try { _map.Save(_path); } catch (Exception ex) { Log("Could not update the map's package name: " + ex.Message); MarkDirty(); }
            }
        }
        UpdateTitle();
        Log($"Renamed map {oldName}.bin to {name}.bin{(_dirty ? " (unsaved changes still pending — Ctrl+S saves to the new name)" : "")}.");
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        return MessageBox.Show(this, "Discard unsaved changes?", AppInfo.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private void ChooseCelesteDir()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select your Celeste install folder (the one containing Celeste.dll and Mods)",
            UseDescriptionForTitle = true,
            InitialDirectory = GameLink.FindCelesteDir() ?? "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (!GameLink.IsCelesteDir(dlg.SelectedPath))
        {
            MessageBox.Show(this, "That folder has no Celeste.dll and Mods folder. Install Everest first (via Olympus) and pick the game folder.",
                AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _settings.CelesteDir = dlg.SelectedPath;
        _settings.Save();
        MessageBox.Show(this, $"Saved. Restart {AppInfo.Title} to load vanilla and mod assets from:\n{dlg.SelectedPath}",
            AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void MarkDirty()
    {
        if (!_dirty) { _dirty = true; UpdateTitle(); }
    }

    private void UpdateTitle() =>
        Text = _path == null ? AppInfo.Title : $"{(_dirty ? "* " : "")}{Path.GetFileName(_path)} — {_path} — {AppInfo.Title}";

    // ---- rooms ----

    private void RebuildRooms(bool keepView)
    {
        if (_map == null) return;
        var rooms = MapModel.Levels(_map).Children.Select(l => new Room(l)).ToList();
        _canvas.SetRooms(rooms, keepView);
        RefreshRoomList();
    }

    private bool _suppressListFocus;

    private string GroupOf(string name)
    {
        int i = name.IndexOfAny(new[] { '-', '_' });
        string prefix = i > 0 ? name[..i] : name;
        return MergedGroup(prefix, _path != null ? Path.GetFileNameWithoutExtension(_path) : null)
            ?? MergedGroup(prefix, "*") ?? prefix;
    }

    /// <summary>Group a prefix is merged into for a map (AppSettings.RoomGroupMerges), or null.</summary>
    private string? MergedGroup(string prefix, string? mapKey)
    {
        if (mapKey == null) return null;
        var merges = _settings.RoomGroupMerges?.FirstOrDefault(kv => string.Equals(kv.Key, mapKey, StringComparison.OrdinalIgnoreCase)).Value;
        return merges?.FirstOrDefault(kv => string.Equals(kv.Key, prefix, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>Rooms grouped by their name prefix ("boss-00", "boss-01" → "boss"); a filter shows matches expanded.</summary>
    private void RefreshRoomList()
    {
        string f = _filter.Text.Trim();
        var expanded = _roomTree.Nodes.Cast<TreeNode>().Where(n => n.IsExpanded).Select(n => n.Text).ToHashSet();
        var rooms = _canvas.Rooms.Where(r => f.Length == 0 || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _roomTree.BeginUpdate();
        _roomTree.Nodes.Clear();
        foreach (var g in rooms.GroupBy(r => GroupOf(r.Name), StringComparer.OrdinalIgnoreCase))
        {
            var list = g.ToList();
            if (list.Count == 1) { _roomTree.Nodes.Add(new TreeNode(list[0].Name) { Tag = list[0] }); continue; }
            var node = new TreeNode($"{g.Key}  ({list.Count})");
            foreach (var r in list) node.Nodes.Add(new TreeNode(r.Name) { Tag = r });
            _roomTree.Nodes.Add(node);
            if (f.Length > 0 || expanded.Contains(node.Text)) node.Expand();
        }
        _roomTree.EndUpdate();
        if (_canvas.Selected?.Room is { } sel) SelectRoomInList(sel);
    }

    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
        {
            yield return n;
            foreach (var c in AllNodes(n.Nodes)) yield return c;
        }
    }

    private void SelectRoomInList(Room r)
    {
        if (_roomTree.SelectedNode?.Tag == r) return;
        _suppressListFocus = true;
        var node = AllNodes(_roomTree.Nodes).FirstOrDefault(n => n.Tag == r);
        if (node != null) { _roomTree.SelectedNode = node; node.EnsureVisible(); }
        _suppressListFocus = false;
    }

    private Room? FindRoom(string name)
    {
        if (name.StartsWith("lvl_")) name = name[4..];
        return _canvas.Rooms.FirstOrDefault(r => r.Name == name);
    }

    private Room? SelectedRoom() => _canvas.Selected?.Room ?? _roomTree.SelectedNode?.Tag as Room;

    private PointF LocalOf(Room r, Point canvasPoint)
    {
        var w = _canvas.ToWorld(canvasPoint);
        return new PointF(w.X - r.X, w.Y - r.Y);
    }

    // ---- undo ----

    private void PushUndo()
    {
        if (_map == null) return;
        _undo.Add(_map.Root.Clone());
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Undo() => Swap(_undo, _redo, "Undo");
    private void Redo() => Swap(_redo, _undo, "Redo");

    private void Swap(List<BinElement> from, List<BinElement> to, string what)
    {
        if (_map == null || from.Count == 0) return;
        to.Add(_map.Root.Clone());
        _map.Root = from[^1];
        from.RemoveAt(from.Count - 1);
        _canvas.Select(null);
        RebuildRooms(keepView: true);
        MarkDirty();
        _status.Text = what;
    }

    // ---- editing ----

    private void AddElement(Room room, PointF world, bool trigger)
    {
        if (_map == null) return;
        string? name = Prompt(trigger ? "Trigger name (e.g. eventTrigger, CEL/…)" : "Entity name (e.g. spikesUp, refill, CEL/…)",
            trigger ? "eventTrigger" : "refill");
        if (string.IsNullOrWhiteSpace(name)) return;
        PushUndo();
        var el = new BinElement(name.Trim());
        el.Set("id", MapModel.NextId(_map));
        el.Set("x", MapModel.Snap8(world.X - room.X));
        el.Set("y", MapModel.Snap8(world.Y - room.Y));
        if (trigger) { el.Set("width", 16); el.Set("height", 16); }
        (trigger ? room.TriggersEl : room.EntitiesEl).Children.Add(el);
        _canvas.Select(new Selection(room, el, trigger));
        MarkDirty();
    }

    private void NewRoom(Rectangle? rect)
    {
        if (_map == null) { Log("Open a map first."); return; }
        var r = rect ?? new Rectangle(MapModel.Snap8(_canvas.ViewCenter.X - 160), MapModel.Snap8(_canvas.ViewCenter.Y - 92), 320, 184);
        string? name = Prompt($"Name for the new {r.Width}x{r.Height} room", MapModel.UniqueName(_map, "room-00"));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (FindRoom(name) != null) name = MapModel.UniqueName(_map, name);
        PushUndo();
        var level = MapModel.NewLevel(name, r.X, r.Y, r.Width, r.Height);
        MapModel.Levels(_map).Children.Add(level);
        RebuildRooms(keepView: true);
        if (_canvas.Rooms.FirstOrDefault(x => x.El == level) is { } room) _canvas.Select(Selection.OfRoom(room));
        MarkDirty();
        Log($"Added room {name}.");
    }

    private void DuplicateRoom(Room? room)
    {
        if (_map == null || room == null) return;
        PushUndo();
        // place the copy to the right of everything on the same row band
        int x = _canvas.Rooms.Where(r => r.Y < room.Y + room.H && room.Y < r.Y + r.H).Max(r => r.X + r.W) + 8;
        var copy = MapModel.DuplicateLevel(_map, room.El, x, room.Y);
        RebuildRooms(keepView: true);
        if (_canvas.Rooms.FirstOrDefault(r => r.El == copy) is { } nr) { _canvas.Select(Selection.OfRoom(nr)); _canvas.FocusRoom(nr); }
        MarkDirty();
        Log($"Duplicated {room.Name} as {copy.Str("name")}.");
    }

    private void RenameRoom(Room? room)
    {
        if (_map == null || room == null) { Log("Select a room first."); return; }
        string? input = Prompt($"New name for room {room.Name}", room.Name);
        if (input == null) return;
        string name = input.Trim();
        if (name.StartsWith("lvl_")) name = name[4..];
        if (name.Length == 0 || name == room.Name) return;
        if (_canvas.Rooms.Any(r => r.El != room.El && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"A room named '{name}' already exists.", AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string old = room.Name;
        PushUndo();
        room.El.Set("name", (room.RawName.StartsWith("lvl_") ? "lvl_" : "") + name);
        RebuildRooms(keepView: true);
        if (_canvas.Rooms.FirstOrDefault(r => r.El == room.El) is { } nr) { _canvas.Select(Selection.OfRoom(nr)); SelectRoomInList(nr); }
        MarkDirty();
        Log($"Renamed room {old} to {name}. Teleports, checkpoints or triggers that name the old room are not changed.");
    }

    private void ResizeRoomDialog(Room? room)
    {
        if (_map == null || room == null) { Log("Select a room first."); return; }
        using var f = new ResizeRoomForm(room);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        if (f.EdgeLeft == 0 && f.EdgeTop == 0 && f.EdgeRight == 0 && f.EdgeBottom == 0) return;
        PushUndo();
        RoomSizing.Result res;
        try { res = RoomSizing.Resize(_map, room, f.EdgeLeft, f.EdgeTop, f.EdgeRight, f.EdgeBottom, f.FillMode, '1', f.PushRooms); }
        catch (ArgumentException ex) { Log(ex.Message); return; }
        RebuildRooms(keepView: true);
        if (_canvas.Rooms.FirstOrDefault(r => r.El == room.El) is { } nr) _canvas.Select(Selection.OfRoom(nr));
        MarkDirty();
        Log($"Resized {room.Name} to {res.New.Width}x{res.New.Height} [{RoomSizing.TierOf(res.New.Width, res.New.Height)}]" +
            (res.Pushed.Count > 0 ? $", pushed {res.Pushed.Count} room(s)" : "") +
            (res.Overlaps.Count > 0 ? $"; overlaps {string.Join(", ", res.Overlaps)}" : "") + ".");
    }

    private void DeleteRoom(Room? room)
    {
        if (_map == null || room == null) return;
        if (MessageBox.Show(this, $"Delete room {room.Name} and everything in it?", AppInfo.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        PushUndo();
        MapModel.Levels(_map).Children.Remove(room.El);
        _canvas.Select(null);
        RebuildRooms(keepView: true);
        MarkDirty();
        Log($"Deleted room {room.Name} (Ctrl+Z restores it).");
    }

    private void ShowContextMenu(Point screen, PointF world, Room? room)
    {
        var menu = new ContextMenuStrip();
        if (room != null)
        {
            var local = new PointF(world.X - room.X, world.Y - room.Y);
            menu.Items.Add($"Teleport to {room.Name} (nearest spawn)", null, async (_, _) => await TeleportAsync(room, local, false));
            menu.Items.Add($"Teleport to {room.Name} (exactly here)", null, async (_, _) => await TeleportAsync(room, local, true));
            menu.Items.Add($"Rename room {room.Name}…", null, (_, _) => RenameRoom(room));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Add entity here…", null, (_, _) => AddElement(room, world, false));
            menu.Items.Add("Add trigger here…", null, (_, _) => AddElement(room, world, true));
        }
        if (_canvas.Selected is { El: not null })
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Cut", null, (_, _) => CutSelected());
            menu.Items.Add("Copy", null, (_, _) => CopySelected());
            menu.Items.Add("Duplicate", null, (_, _) => Duplicate());
            menu.Items.Add("Delete", null, (_, _) => DeleteSelected());
            menu.Items.Add("Flip horizontally", null, (_, _) => FlipSelected(true));
            menu.Items.Add("Flip vertically", null, (_, _) => FlipSelected(false));
            if (!_canvas.Selected.Decal) menu.Items.Add("Add node", null, (_, _) => AddNode());
        }
        if (ClipboardHasItems())
        {
            var at = world;
            menu.Items.Add("Paste here", null, (_, _) => Paste(at));
        }
        menu.Items.Add(new ToolStripSeparator());
        if (room != null)
        {
            menu.Items.Add($"Duplicate room {room.Name}", null, (_, _) => DuplicateRoom(room));
            menu.Items.Add($"Delete room {room.Name}…", null, (_, _) => DeleteRoom(room));
            menu.Items.Add($"Screenshot room {room.Name}…", null, async (_, _) => await ScreenshotAsync(ShotKind.Room, room));
        }
        else if (_map != null)
        {
            var at = new Rectangle(MapModel.Snap8(world.X), MapModel.Snap8(world.Y), 320, 184);
            menu.Items.Add("New room here…", null, (_, _) => NewRoom(at));
        }
        menu.Items.Add("Generate rooms (PCG)…", null, (_, _) => GenerateRooms());
        menu.Show(_canvas, screen);
    }

    private string? Prompt(string label, string initial)
    {
        using var f = new Form { Text = AppInfo.Title, Size = new Size(420, 140), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var l = new Label { Text = label, Left = 10, Top = 10, Width = 390 };
        var t = new TextBox { Text = initial, Left = 10, Top = 32, Width = 385 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 240, Top = 64, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 320, Top = 64, Width = 75 };
        f.Controls.AddRange(new Control[] { l, t, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? t.Text : null;
    }

    // ---- game link ----

    private async Task PollGameAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var st = await _game.GetStateAsync();
            _lastState = st;
            if (st == null)
            {
                _gameStatus.Text = "game: not running";
                if (_canvas.PlayerWorld != null || _canvas.GameRoom != null) { _canvas.PlayerWorld = null; _canvas.GameRoom = null; _canvas.Invalidate(); }
                return;
            }
            if (!st.InLevel || st.Map == null)
            {
                _gameStatus.Text = "game: not in a level";
                if (_canvas.GameRoom != null) { _canvas.PlayerWorld = null; _canvas.GameRoom = null; _canvas.Invalidate(); }
                return;
            }

            bool sameMap = _path != null && string.Equals(Path.GetFullPath(st.Map), _path, StringComparison.OrdinalIgnoreCase);
            _gameStatus.Text = $"game: {st.Sid} {st.Side} · {st.Room}{(st.Dead ? " (dead)" : "")}{(sameMap ? "" : " (other map)")}";
            if (!sameMap && _follow.Checked && !_dirty && File.Exists(st.Map))
            {
                Open(st.Map);
                _lastGameRoom = null;
                sameMap = true;
            }
            if (!sameMap) { _canvas.GameRoom = null; _canvas.PlayerWorld = null; _canvas.Invalidate(); return; }

            _canvas.GameRoom = st.Room;
            _canvas.PlayerWorld = st.Player;
            if (_follow.Checked && st.Room != _lastGameRoom && st.Room != null && FindRoom(st.Room) is { } r)
            {
                _canvas.FocusRoom(r);
                SelectRoomInList(r);
            }
            _lastGameRoom = st.Room;
            _canvas.Invalidate();
        }
        finally { _polling = false; }
    }

    private void GoToGameRoom()
    {
        if (_lastState?.Room is { } name && FindRoom(name) is { } r) _canvas.FocusRoom(r);
        else Log("The game is not in a room of this map.");
    }

    private async Task TeleportAsync(Room? room, PointF? local, bool exact)
    {
        if (room == null || _path == null) return;
        if (_dirty) Log("Note: unsaved changes — the game plays the last saved version (Ctrl+S saves and reloads).");
        string res = await _game.TeleportAsync(_path, room.Name, local, exact);
        Log($"Teleport {room.Name}: {res}");
        if (res.StartsWith("ERROR: game not reachable")) Log("Start Celeste (Game ▸ Launch Celeste) with Everest's debug mode/RC enabled.");
    }

    private async Task ReloadGameAsync()
    {
        if (_path == null) return;
        string res = await _game.ReloadMapAsync(_path);
        Log("Reload in game: " + res);
    }

    // ---- PCG ----

    private string? _pcgRoot;

    private string? LocatePcgHelper(bool force)
    {
        if (!force && _pcgRoot != null) return _pcgRoot;
        if (!force) _pcgRoot = PcgGenerator.FindLoennRoot(GameLink.FindModDir());
        if (_pcgRoot != null && !force) return _pcgRoot;

        using var dlg = new FolderBrowserDialog { Description = "Select the PCGHelper mod folder (the one containing Loenn/library/selecte)" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return _pcgRoot;
        foreach (string candidate in new[] { Path.Combine(dlg.SelectedPath, "Loenn"), dlg.SelectedPath, Path.GetDirectoryName(dlg.SelectedPath) ?? "" })
            if (File.Exists(Path.Combine(candidate, "library", "selecte", "level.lua")))
            {
                _settings.PcgHelperDir = candidate;
                _settings.Save();
                return _pcgRoot = candidate;
            }
        Log("That folder does not contain Loenn/library/selecte/level.lua.");
        return _pcgRoot;
    }

    private void GenerateRooms()
    {
        if (_map == null)
        {
            Log("Open a map first; generated rooms are added to it.");
            return;
        }
        string? root = LocatePcgHelper(force: false);
        if (root == null)
        {
            Log("PCGHelper not found. Install it next to CEL (Mods/PCGHelper) or use PCG ▸ Locate PCGHelper…");
            return;
        }
        using var form = new PcgForm(new PcgGenerator(root), () => _map, () => (_canvas.FgTiler, _canvas.BgTiler));
        if (form.ShowDialog(this) != DialogResult.OK || form.Result is not { Rooms.Count: > 0 } result) return;

        PushUndo();
        var added = MapModel.ImportPcg(_map, result, form.Prefix);
        RebuildRooms(keepView: true);
        MarkDirty();
        var bounds = added.Select(l => new Rectangle(l.Int("x"), l.Int("y"), l.Int("width"), l.Int("height"))).Aggregate(Rectangle.Union);
        _canvas.FitRect(bounds);
        Log($"Imported {added.Count} generated rooms (seed \"{result.Seed}\"): {string.Join(", ", added.Select(l => l.Str("name")))}. " +
            "Save (Ctrl+S) to write them; the game sees new rooms after the reload.");
    }

    private void Log(string s)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
        _status.Text = s.Split('\n')[0];
    }
}
