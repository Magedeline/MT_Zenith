using System.Drawing.Drawing2D;

namespace CELMapViewer;

/// <summary>
/// What is selected: an entity, trigger or decal in a room (optionally one of its nodes, <see cref="Node"/> ≥ 0),
/// or the room itself (<see cref="El"/> null).
/// </summary>
public sealed record Selection(Room Room, BinElement? El, bool Trigger, bool Decal = false, int Node = -1)
{
    public bool IsRoom => El == null;
    public bool IsNode => Node >= 0;
    public static Selection OfRoom(Room r) => new(r, null, false);

    /// <summary>The list element holding <see cref="El"/> (entities, triggers, fgdecals or bgdecals).</summary>
    public BinElement? Container => El == null ? null
        : Decal ? (FgDecal ? Room.El.Child("fgdecals") : Room.El.Child("bgdecals"))
        : Trigger ? Room.TriggersEl : Room.EntitiesEl;

    public bool FgDecal => Decal && El != null && Room.El.Child("fgdecals")?.Children.Contains(El) == true;
    public BinElement? NodeEl => Node >= 0 && El != null ? MapCanvas.Nodes(El).ElementAtOrDefault(Node) : null;
    public string Kind => El == null ? "Room" : Decal ? (FgDecal ? "FG decal" : "BG decal") : Trigger ? "Trigger" : "Entity";
}

public enum Tool { Select, Room, Brush, Rect, Fill, Line, Place, Decal }

/// <summary>Which items the select tool picks (Lönn-style layers). All = entities and triggers.</summary>
public enum EditLayer { All, Entities, Triggers, FgDecals, BgDecals }

/// <summary>
/// Pan/zoom view of a map: autotiled rooms rendered in the background and cached with mips, entity sprites,
/// selection and dragging, room move/resize/create, tile brush/rectangle/fill and entity placement, and a minimap.
/// </summary>
public sealed partial class MapCanvas : Control
{
    public List<Room> Rooms { get; private set; } = new();
    public float Zoom { get; private set; } = 0.5f;
    private PointF _origin;              // world point at the control's top-left

    /// <summary>The primary (last clicked) selection; <see cref="SelectedItems"/> holds every selected item.</summary>
    public Selection? Selected { get; private set; }
    public List<Selection> SelectedItems { get; private set; } = new();
    public string? GameRoom;             // highlighted room the game is in
    public List<Rectangle> Fillers = new(); // map filler rectangles (world pixels)
    public bool ShowFillers = true;
    public PointF? PlayerWorld;
    public bool ShowBg = true, ShowEntities = true, ShowTriggers = true, ShowLabels = true, ShowMinimap = true;
    private bool _showFgDecals = true, _showBgDecals = true;
    public bool ShowFgDecals { get => _showFgDecals; set { _showFgDecals = value; RerenderAll(); } }
    public bool ShowBgDecals { get => _showBgDecals; set { _showBgDecals = value; RerenderAll(); } }
    public EditLayer ActiveLayer { get => _layer; set { _layer = value; Invalidate(); } }
    private EditLayer _layer;
    public string? PlaceDecal;           // decal texture for the decal tool ("generic/grass_a")

    public Tool Tool { get => _tool; set { _tool = value; _hoverCell = null; Invalidate(); } }
    private Tool _tool;
    public bool PaintFg = true;
    public char PaintChar = '1';
    public PlacementDef? PlaceItem;
    public Func<int>? NextId;

    private Autotiler? _fgTiler, _bgTiler;
    public Autotiler? FgTiler { get => _fgTiler; set { _fgTiler = value; RerenderAll(); } }
    public Autotiler? BgTiler { get => _bgTiler; set { _bgTiler = value; RerenderAll(); } }

    public event Action? SelectionChanged;
    public event Action? BeforeEdit;     // snapshot for undo
    public event Action? Edited;
    public event Action? RoomsChanged;   // a room was moved/resized (bounds changed)
    public event Action<Room>? RoomActivated;
    public event Action<Point, PointF, Room?>? ContextRequested; // screen, world, room
    public event Action<Rectangle>? CreateRoomRequested;
    public event Action? ViewChanged;
    public event Action<char>? TileCharPicked;

    private enum Drag { None, Pan, Move, RoomMove, RoomResize, RoomCreate, Paint, RectPaint, LinePaint, Minimap, BoxSelect, ItemResize }
    private Drag _drag;
    private Point _downScreen, _lastScreen;
    private PointF _dragStartWorld;
    private List<(BinElement El, float X, float Y)> _dragOriginal = new();
    private readonly HashSet<Room> _dragDecalRooms = new();
    private bool _dragMoved;
    private bool _spaceDown;
    private PointF? _hoverWorld;

    // box select
    private bool _boxAdd;
    private RectangleF _boxRect;

    // item resize (also drag-to-size when placing)
    private Selection? _resizeItem;
    private RectangleF _resizeOrig;      // room-local x, y, width, height
    private bool _placeResize;

    // room tool
    private Room? _dragRoom;
    private int _resizeEdges;            // 1 = left, 2 = top, 4 = right, 8 = bottom
    private Rectangle _roomPreview;

    // tile tools
    private Point? _hoverCell, _lastCell;   // world tile coordinates
    private Point _rectStart;
    private char _strokeChar;
    private readonly HashSet<Room> _strokeRooms = new();
    private readonly Dictionary<Room, Dictionary<Point, char>> _pendingCells = new();

    // render cache
    private long _frame;
    private const long PixelBudget = 48L * 1024 * 1024;
    private bool _invalidatePending;
    private readonly Action _workerDone;

    private static readonly Font LabelFont = new("Segoe UI", 9f);
    private static readonly Font SmallFont = new("Segoe UI", 7.5f);
    private const int MiniW = 220, MiniH = 150, MiniMargin = 10;

    public MapCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Themes.Current.Canvas;
        _workerDone = RequestRepaint;
        AssetWorker.Completed += _workerDone;
    }

    protected override void Dispose(bool disposing)
    {
        AssetWorker.Completed -= _workerDone;
        if (disposing) foreach (var r in Rooms) r.DisposeImage();
        base.Dispose(disposing);
    }

    /// <summary>Repaints soon; safe to call from any thread, coalesced.</summary>
    public void RequestRepaint()
    {
        if (_invalidatePending || !IsHandleCreated || IsDisposed) return;
        _invalidatePending = true;
        try { BeginInvoke(() => { _invalidatePending = false; Invalidate(); }); }
        catch { _invalidatePending = false; }
    }

    public void SetRooms(List<Room> rooms, bool keepView)
    {
        foreach (var r in Rooms.Except(rooms)) r.DisposeImage();
        _pendingCells.Clear();
        Rooms = rooms;
        if (Selected != null && !rooms.Contains(Selected.Room)) Select(null);
        else if (SelectedItems.Any(s => !rooms.Contains(s.Room)))
            SelectMany(SelectedItems.Where(s => rooms.Contains(s.Room)).ToList());
        if (!keepView) FitAll();
        Invalidate();
    }

    public void Select(Selection? s)
    {
        Selected = s;
        SelectedItems = s?.El != null ? new() { s } : new();
        SelectionChanged?.Invoke();
        Invalidate();
    }

    /// <summary>Selects several items; <paramref name="primary"/> (default: the last one) drives the property grid.</summary>
    public void SelectMany(IEnumerable<Selection> items, Selection? primary = null)
    {
        var list = items.Where(s => s.El != null).Distinct().ToList();
        if (list.Count == 0) { Select(null); return; }
        Selected = primary != null && list.Contains(primary) ? primary : list[^1];
        SelectedItems = list;
        SelectionChanged?.Invoke();
        Invalidate();
    }

    public void ToggleSelect(Selection s)
    {
        if (s.El == null) return;
        var list = SelectedItems.ToList();
        if (!list.Remove(s)) list.Add(s);
        SelectMany(list, list.Contains(s) ? s : null);
    }

    private void RerenderAll()
    {
        foreach (var r in Rooms) { r.InvalidateRender(); r.RequestedVersion = -1; }
        Invalidate();
    }

    // ---- view ----

    public PointF ToWorld(Point p) => new(_origin.X + p.X / Zoom, _origin.Y + p.Y / Zoom);
    public PointF ToScreen(PointF w) => new((w.X - _origin.X) * Zoom, (w.Y - _origin.Y) * Zoom);
    public PointF ViewCenter => ToWorld(new Point(Width / 2, Height / 2));

    public void FitAll()
    {
        if (Rooms.Count == 0) { _origin = PointF.Empty; Zoom = 0.5f; Invalidate(); ViewChanged?.Invoke(); return; }
        FitRect(MapBounds());
    }

    private Rectangle MapBounds() => Rooms.Select(r => r.Bounds).Aggregate(Rectangle.Union);

    public void FitRect(RectangleF b)
    {
        if (Width < 10 || Height < 10) return;
        Zoom = Math.Clamp(Math.Min(Width / (b.Width + 64), Height / (b.Height + 64)), 0.02f, 8f);
        CenterOn(new PointF(b.X + b.Width / 2, b.Y + b.Height / 2));
    }

    public void CenterOn(PointF world)
    {
        _origin = new PointF(world.X - Width / 2f / Zoom, world.Y - Height / 2f / Zoom);
        Invalidate();
        ViewChanged?.Invoke();
    }

    public void FocusRoom(Room r) => FitRect(r.Bounds);

    public void SetZoom(float zoom)
    {
        var c = ViewCenter;
        Zoom = Math.Clamp(zoom, 0.02f, 8f);
        CenterOn(c);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var before = ToWorld(e.Location);
        Zoom = Math.Clamp(Zoom * (e.Delta > 0 ? 1.25f : 0.8f), 0.02f, 8f);
        _origin = new PointF(before.X - e.X / Zoom, before.Y - e.Y / Zoom);
        Invalidate();
        ViewChanged?.Invoke();
    }

    // ---- hit testing ----

    public Room? RoomAt(PointF w) => Rooms.LastOrDefault(r => r.Bounds.Contains(Point.Truncate(w)));

    /// <summary>The topmost item (or node of an item) on the active layer under a world point.</summary>
    public Selection? HitTest(PointF w)
    {
        float slop = 3 / Zoom;
        foreach (var room in Enumerable.Reverse(Rooms))
        {
            var rb = room.Bounds; rb.Inflate(64, 64);
            if (!rb.Contains(Point.Truncate(w))) continue;
            foreach (var s in Enumerable.Reverse(ItemsOf(room)))
            {
                if (!s.Decal)
                {
                    var nodes = Nodes(s.El!).ToList();
                    for (int i = nodes.Count - 1; i >= 0; i--)
                        if (Math.Abs(room.X + nodes[i].Num("x") - w.X) < 4 + slop && Math.Abs(room.Y + nodes[i].Num("y") - w.Y) < 4 + slop)
                            return s with { Node = i };
                }
                var b = BoxOf(s);
                b.Inflate(slop, slop);
                if (b.Contains(w)) return s;
            }
        }
        return null;
    }

    /// <summary>Which edges of a room the screen point is on (room tool resize handles).</summary>
    private int EdgesAt(Room r, Point screen)
    {
        var tl = ToScreen(new PointF(r.X, r.Y));
        var br = ToScreen(new PointF(r.X + r.W, r.Y + r.H));
        const float m = 6;
        if (screen.X < tl.X - m || screen.X > br.X + m || screen.Y < tl.Y - m || screen.Y > br.Y + m) return 0;
        int edges = 0;
        if (Math.Abs(screen.X - tl.X) <= m) edges |= 1;
        if (Math.Abs(screen.Y - tl.Y) <= m) edges |= 2;
        if (Math.Abs(screen.X - br.X) <= m) edges |= 4;
        if (Math.Abs(screen.Y - br.Y) <= m) edges |= 8;
        return edges;
    }

    private static Cursor EdgeCursor(int edges) => edges switch
    {
        1 or 4 => Cursors.SizeWE,
        2 or 8 => Cursors.SizeNS,
        3 or 12 => Cursors.SizeNWSE,
        6 or 9 => Cursors.SizeNESW,
        _ => Cursors.SizeAll
    };

    private static Point CellOf(PointF w) => new((int)MathF.Floor(w.X / 8), (int)MathF.Floor(w.Y / 8));

    private Rectangle MinimapRect => new(Width - MiniW - MiniMargin, Height - MiniH - MiniMargin, MiniW, MiniH);
    private bool MinimapVisible => ShowMinimap && Rooms.Count > 0 && Width > MiniW * 2 && Height > MiniH * 2;

    // ---- mouse ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        _downScreen = _lastScreen = e.Location;
        _dragMoved = false;
        var w = ToWorld(e.Location);

        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && _spaceDown)) { _drag = Drag.Pan; return; }
        if (MinimapVisible && MinimapRect.Contains(e.Location) && e.Button == MouseButtons.Left)
        {
            _drag = Drag.Minimap;
            MinimapNavigate(e.Location);
            return;
        }

        bool tileTool = _tool is Tool.Brush or Tool.Rect or Tool.Fill or Tool.Line;
        if (e.Button == MouseButtons.Right && !tileTool)
        {
            var hit = HitTest(w);
            if (hit != null) { if (!SelectedItems.Contains(hit)) Select(hit); else SetPrimary(hit); }
            else if (RoomAt(w) is { } rr) Select(Selection.OfRoom(rr));
            ContextRequested?.Invoke(e.Location, w, RoomAt(w));
            return;
        }
        if (e.Button != MouseButtons.Left && !(tileTool && e.Button == MouseButtons.Right)) return;

        switch (_tool)
        {
            case Tool.Select: BeginSelectDrag(e.Location, w); break;
            case Tool.Room: BeginRoomDrag(e.Location, w); break;
            case Tool.Place: PlaceAt(w); break;
            case Tool.Decal: PlaceDecalAt(w); break;
            case Tool.Brush:
            case Tool.Rect:
            case Tool.Fill:
            case Tool.Line:
                BeginTileTool(e, w);
                break;
        }
    }

    private void BeginSelectDrag(Point screen, PointF w)
    {
        _dragStartWorld = w;
        if (SelectedItems.Count == 1 && Selected is { El: not null, IsNode: false, Decal: false } one && ItemEdgesAt(one, screen) is var edges and not 0)
        {
            BeginItemResize(one, edges);
            return;
        }

        var s = HitTest(w);
        bool modifier = (ModifierKeys & (Keys.Control | Keys.Shift)) != 0;
        if (s?.El != null)
        {
            if (modifier) { ToggleSelect(s); return; }
            if (!SelectedItems.Contains(s)) Select(s); else SetPrimary(s);
            _drag = Drag.Move;
            CaptureDragOriginals();
        }
        else
        {
            _boxAdd = modifier;
            _boxRect = RectangleF.Empty;
            _drag = Drag.BoxSelect;
        }
    }

    /// <summary>Remembers the positions of everything that moves with the selection (the primary item first, for snapping).</summary>
    private void CaptureDragOriginals()
    {
        _dragOriginal = new();
        _dragDecalRooms.Clear();
        var seen = new HashSet<BinElement>(ReferenceEqualityComparer.Instance);
        void Add(BinElement el) { if (seen.Add(el)) _dragOriginal.Add((el, el.Num("x"), el.Num("y"))); }
        foreach (var s in SelectedItems.OrderBy(s => s == Selected ? 0 : 1))
        {
            if (s.IsNode) { if (s.NodeEl is { } n) Add(n); continue; }
            Add(s.El!);
            if (s.Decal) _dragDecalRooms.Add(s.Room);
            else foreach (var n in Nodes(s.El!)) Add(n);
        }
    }

    private void SetPrimary(Selection s)
    {
        if (Selected == s) return;
        Selected = s;
        SelectionChanged?.Invoke();
        Invalidate();
    }

    private void BeginRoomDrag(Point screen, PointF w)
    {
        _dragStartWorld = w;
        foreach (var r in Enumerable.Reverse(Rooms))
        {
            int edges = EdgesAt(r, screen);
            if (edges != 0)
            {
                _dragRoom = r; _resizeEdges = edges; _roomPreview = r.Bounds;
                _drag = Drag.RoomResize;
                Select(Selection.OfRoom(r));
                return;
            }
        }
        if (RoomAt(w) is { } room)
        {
            _dragRoom = room; _roomPreview = room.Bounds;
            _drag = Drag.RoomMove;
            Select(Selection.OfRoom(room));
            return;
        }
        _drag = Drag.RoomCreate;
        var c = CellOf(w);
        _roomPreview = new Rectangle(c.X * 8, c.Y * 8, 8, 8);
        Invalidate();
    }

    private void PlaceAt(PointF w)
    {
        if (PlaceItem == null) return;
        var room = RoomAt(w);
        if (room == null) return;
        BeforeEdit?.Invoke();
        var el = PlaceItem.Create();
        el.Set("id", NextId?.Invoke() ?? 0);
        bool free = (ModifierKeys & Keys.Alt) != 0;
        float x = w.X - room.X, y = w.Y - room.Y;
        if (PlaceItem.Plugin.Trigger || el.Int("width") > 0 || el.Int("height") > 0)
        {
            // rectangles are placed by their top-left corner on the clicked cell
            x = MathF.Floor(x / 8) * 8; y = MathF.Floor(y / 8) * 8;
        }
        else if (!free) { x = MapModel.Snap8(x); y = MapModel.Snap8(y); }
        SetNum(el, "x", MathF.Round(x));
        SetNum(el, "y", MathF.Round(y));
        for (int i = 1; i <= PlaceItem.Plugin.MinNodes; i++)
        {
            var n = new BinElement("node");
            SetNum(n, "x", MathF.Round(x) + 16 * i);
            SetNum(n, "y", MathF.Round(y));
            el.Children.Add(n);
        }
        (PlaceItem.Plugin.Trigger ? room.TriggersEl : room.EntitiesEl).Children.Add(el);
        var sel = new Selection(room, el, PlaceItem.Plugin.Trigger);
        Select(sel);
        if (el.Find("width") != null || el.Find("height") != null)
        {
            // drag out the size like Lönn/Rysy; a plain click keeps the placement's default size
            BeginItemResize(sel, (el.Find("width") != null ? 4 : 0) | (el.Find("height") != null ? 8 : 0));
            _placeResize = true;
            _dragMoved = true;
        }
        else Edited?.Invoke();
    }

    private void BeginTileTool(MouseEventArgs e, PointF w)
    {
        var cell = CellOf(w);
        if ((ModifierKeys & Keys.Control) != 0 && e.Button == MouseButtons.Left)
        {
            if (RoomAt(w) is { } r)
            {
                char c = r.Grid(PaintFg)[cell.X - r.X / 8, cell.Y - r.Y / 8];
                if (c != '0') { PaintChar = c; TileCharPicked?.Invoke(c); }
            }
            return;
        }
        _strokeChar = e.Button == MouseButtons.Right ? '0' : PaintChar;
        if (_tool == Tool.Fill)
        {
            if (RoomAt(w) is not { } room) return;
            BeforeEdit?.Invoke();
            if (room.Grid(PaintFg).Fill(cell.X - room.X / 8, cell.Y - room.Y / 8, _strokeChar))
            {
                room.CommitGrid(PaintFg);
                Edited?.Invoke();
            }
            Invalidate();
            return;
        }
        _strokeRooms.Clear();
        _drag = _tool switch { Tool.Brush => Drag.Paint, Tool.Line => Drag.LinePaint, _ => Drag.RectPaint };
        BeforeEdit?.Invoke();
        if (_tool == Tool.Brush) { _lastCell = cell; SetCell(cell, _strokeChar); }
        else _rectStart = cell;
        _hoverCell = cell;
        Invalidate();
    }

    private void SetCell(Point cell, char c)
    {
        var room = RoomAt(new PointF(cell.X * 8 + 4, cell.Y * 8 + 4));
        if (room == null) return;
        var g = room.Grid(PaintFg);
        int x = cell.X - room.X / 8, y = cell.Y - room.Y / 8;
        if (x < 0 || y < 0 || x >= g.Cols || y >= g.Rows || g[x, y] == c) return;
        g[x, y] = c;
        _strokeRooms.Add(room);
        if (!_pendingCells.TryGetValue(room, out var cells)) _pendingCells[room] = cells = new();
        cells[cell] = c;
        room.InvalidateRender();
    }

    private void PaintLine(Point a, Point b)
    {
        foreach (var c in LineCells(a, b)) SetCell(c, _strokeChar);
    }

    public static IEnumerable<Point> LineCells(Point a, Point b)
    {
        int dx = Math.Abs(b.X - a.X), dy = -Math.Abs(b.Y - a.Y), sx = a.X < b.X ? 1 : -1, sy = a.Y < b.Y ? 1 : -1, err = dx + dy;
        while (true)
        {
            yield return a;
            if (a == b) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; a.X += sx; }
            if (e2 <= dx) { err += dx; a.Y += sy; }
        }
    }

    private void EndStroke()
    {
        foreach (var r in _strokeRooms) r.CommitGrid(PaintFg);
        bool changed = _strokeRooms.Count > 0;
        _strokeRooms.Clear();
        if (changed) Edited?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var w = ToWorld(e.Location);
        bool moved = Math.Abs(e.X - _downScreen.X) + Math.Abs(e.Y - _downScreen.Y) >= 4;
        if (_tool is Tool.Place or Tool.Decal && _drag == Drag.None) { _hoverWorld = w; Invalidate(); }
        switch (_drag)
        {
            case Drag.BoxSelect:
                if (!_dragMoved && !moved) return;
                _dragMoved = true;
                _boxRect = RectangleF.FromLTRB(Math.Min(_dragStartWorld.X, w.X), Math.Min(_dragStartWorld.Y, w.Y),
                                               Math.Max(_dragStartWorld.X, w.X), Math.Max(_dragStartWorld.Y, w.Y));
                Invalidate();
                return;

            case Drag.ItemResize when _resizeItem?.El != null:
                if (!moved && (_placeResize || !_dragMoved)) return;
                if (!_dragMoved) { BeforeEdit?.Invoke(); _dragMoved = true; }
                ApplyItemResize(w);
                Invalidate();
                return;

            case Drag.LinePaint:
                _hoverCell = CellOf(w);
                Invalidate();
                return;

            case Drag.Pan:
                _origin.X -= (e.X - _lastScreen.X) / Zoom;
                _origin.Y -= (e.Y - _lastScreen.Y) / Zoom;
                _lastScreen = e.Location;
                Invalidate();
                ViewChanged?.Invoke();
                return;

            case Drag.Minimap:
                MinimapNavigate(e.Location);
                return;

            case Drag.Move when Selected?.El != null:
            {
                if (!_dragMoved && !moved) return;
                if (!_dragMoved) { BeforeEdit?.Invoke(); _dragMoved = true; }
                float dx = w.X - _dragStartWorld.X, dy = w.Y - _dragStartWorld.Y;
                bool free = (ModifierKeys & Keys.Alt) != 0;
                var (_, ox, oy) = _dragOriginal[0];
                float nx = ox + dx, ny = oy + dy;
                if (!free) { nx = MapModel.Snap8(nx); ny = MapModel.Snap8(ny); }
                else { nx = MathF.Round(nx); ny = MathF.Round(ny); }
                float sx = nx - ox, sy = ny - oy;
                foreach (var (el, x, y) in _dragOriginal)
                {
                    SetNum(el, "x", x + sx);
                    SetNum(el, "y", y + sy);
                }
                foreach (var r in _dragDecalRooms) r.InvalidateRender();
                Invalidate();
                return;
            }

            case Drag.RoomMove when _dragRoom != null:
                if (!_dragMoved && !moved) return;
                _dragMoved = true;
                _roomPreview.Location = new Point(_dragRoom.X + MapModel.Snap8(w.X - _dragStartWorld.X),
                                                  _dragRoom.Y + MapModel.Snap8(w.Y - _dragStartWorld.Y));
                Invalidate();
                return;

            case Drag.RoomResize when _dragRoom != null:
            {
                _dragMoved = true;
                var b = _dragRoom.Bounds;
                int dx = MapModel.Snap8(w.X - _dragStartWorld.X), dy = MapModel.Snap8(w.Y - _dragStartWorld.Y);
                int l = b.Left, t = b.Top, r = b.Right, bt = b.Bottom;
                if ((_resizeEdges & 1) != 0) l = Math.Min(b.Left + dx, r - 8);
                if ((_resizeEdges & 2) != 0) t = Math.Min(b.Top + dy, bt - 8);
                if ((_resizeEdges & 4) != 0) r = Math.Max(b.Right + dx, l + 8);
                if ((_resizeEdges & 8) != 0) bt = Math.Max(b.Bottom + dy, t + 8);
                _roomPreview = Rectangle.FromLTRB(l, t, r, bt);
                Invalidate();
                return;
            }

            case Drag.RoomCreate:
            {
                var a = CellOf(_dragStartWorld); var c = CellOf(w);
                _roomPreview = Rectangle.FromLTRB(Math.Min(a.X, c.X) * 8, Math.Min(a.Y, c.Y) * 8,
                                                  (Math.Max(a.X, c.X) + 1) * 8, (Math.Max(a.Y, c.Y) + 1) * 8);
                Invalidate();
                return;
            }

            case Drag.Paint:
            {
                var cell = CellOf(w);
                if (_lastCell is { } last && last != cell) PaintLine(last, cell);
                _lastCell = cell; _hoverCell = cell;
                Invalidate();
                return;
            }

            case Drag.RectPaint:
                _hoverCell = CellOf(w);
                Invalidate();
                return;
        }

        // hover feedback
        if (_spaceDown) Cursor = Cursors.SizeAll;
        else if (_tool is Tool.Brush or Tool.Rect or Tool.Fill or Tool.Line)
        {
            var cell = CellOf(w);
            if (_hoverCell != cell) { _hoverCell = cell; Invalidate(); }
            Cursor = Cursors.Cross;
        }
        else if (_tool == Tool.Room)
        {
            int edges = 0;
            foreach (var r in Enumerable.Reverse(Rooms)) if ((edges = EdgesAt(r, e.Location)) != 0) break;
            Cursor = edges != 0 ? EdgeCursor(edges) : RoomAt(w) != null ? Cursors.SizeAll : Cursors.Cross;
        }
        else if (_tool is Tool.Place or Tool.Decal)
        {
            Cursor = RoomAt(w) != null ? Cursors.Cross : Cursors.No;
            var cell = CellOf(w);
            if (_hoverCell != cell) { _hoverCell = cell; Invalidate(); }
        }
        else
        {
            int edges = SelectedItems.Count == 1 && Selected is { El: not null, IsNode: false, Decal: false } one ? ItemEdgesAt(one, e.Location) : 0;
            Cursor = edges != 0 ? EdgeCursor(edges) : HitTest(w) != null ? Cursors.Hand : Cursors.Default;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var drag = _drag;
        _drag = Drag.None;
        switch (drag)
        {
            case Drag.Move when _dragMoved:
                Edited?.Invoke();
                break;

            case Drag.BoxSelect when !_dragMoved:
                if (_boxAdd) break;
                var room = RoomAt(ToWorld(e.Location));
                bool change = room == null ? Selected != null : Selected?.El != null || Selected?.Room != room;
                if (change) Select(room == null ? null : Selection.OfRoom(room));
                break;

            case Drag.BoxSelect:
            {
                var items = ItemsInRect(_boxRect);
                if (_boxAdd) SelectMany(SelectedItems.Concat(items).ToList(), Selected);
                else SelectMany(items);
                _boxRect = RectangleF.Empty;
                break;
            }

            case Drag.ItemResize when _dragMoved:
                Edited?.Invoke();
                SelectionChanged?.Invoke();
                break;

            case Drag.LinePaint when _hoverCell is { } lineEnd:
                PaintLine(_rectStart, lineEnd);
                EndStroke();
                break;

            case Drag.RoomMove when _dragMoved && _dragRoom != null && _roomPreview.Location != _dragRoom.Bounds.Location:
                BeforeEdit?.Invoke();
                _dragRoom.El.Set("x", _roomPreview.X);
                _dragRoom.El.Set("y", _roomPreview.Y);
                Edited?.Invoke();
                RoomsChanged?.Invoke();
                break;

            case Drag.RoomResize when _dragMoved && _dragRoom != null && _roomPreview != _dragRoom.Bounds:
                BeforeEdit?.Invoke();
                MapModel.ResizeRoom(_dragRoom, _roomPreview);
                Edited?.Invoke();
                RoomsChanged?.Invoke();
                break;

            case Drag.RoomCreate when _roomPreview.Width >= 16 && _roomPreview.Height >= 16:
                CreateRoomRequested?.Invoke(_roomPreview);
                break;

            case Drag.Paint:
                EndStroke();
                break;

            case Drag.RectPaint when _hoverCell is { } end:
            {
                int x0 = Math.Min(_rectStart.X, end.X), x1 = Math.Max(_rectStart.X, end.X);
                int y0 = Math.Min(_rectStart.Y, end.Y), y1 = Math.Max(_rectStart.Y, end.Y);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        SetCell(new Point(x, y), _strokeChar);
                EndStroke();
                break;
            }
        }
        _dragRoom = null;
        _lastCell = null;
        _resizeItem = null;
        _placeResize = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverCell != null || _hoverWorld != null) { _hoverCell = null; _hoverWorld = null; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _tool != Tool.Select) return;
        var room = RoomAt(ToWorld(e.Location));
        if (room != null) RoomActivated?.Invoke(room);
    }

    public static void SetNum(BinElement el, string key, float v)
    {
        if (v == MathF.Floor(v)) el.Set(key, (int)v);
        else el.Set(key, v);
    }

    // ---- room images ----

    private int MipLevel() => Zoom >= 0.75f ? 0 : Zoom >= 0.375f ? 1 : Zoom >= 0.1875f ? 2 : 3;

    private void EnsureImage(Room room)
    {
        if (room.RequestedVersion == room.RenderVersion) return;
        room.RequestedVersion = room.RenderVersion;
        var job = RoomRenderJob.From(room, _fgTiler, _bgTiler, _showFgDecals, _showBgDecals);
        AssetWorker.Enqueue(room, () =>
        {
            var img = job.Render();
            try { BeginInvoke(() => Install(room, img)); }
            catch { img.Dispose(); }
        }, high: room.Image != null);
    }

    private void Install(Room room, RoomImage img)
    {
        if (!Rooms.Contains(room) || img.Version > room.RenderVersion || (room.Image != null && room.Image.Version >= img.Version))
        {
            img.Dispose();
            return;
        }
        room.Image?.Dispose();
        room.Image = img;
        img.LastFrame = _frame;
        if (img.Version == room.RenderVersion) _pendingCells.Remove(room);
        EvictOverBudget();
        Invalidate();
    }

    private void EvictOverBudget()
    {
        long total = Rooms.Sum(r => r.Image?.Pixels ?? 0);
        if (total <= PixelBudget) return;
        foreach (var r in Rooms.Where(r => r.Image != null && r.Image.LastFrame < _frame - 1).OrderBy(r => r.Image!.LastFrame).ToList())
        {
            total -= r.Image!.Pixels;
            r.DisposeImage();
            if (total <= PixelBudget) break;
        }
    }

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs pe)
    {
        _frame++;
        var g = pe.Graphics;
        g.Clear(BackColor);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;

        var view = new RectangleF(_origin.X, _origin.Y, Width / Zoom, Height / Zoom);
        g.TranslateTransform(-_origin.X * Zoom, -_origin.Y * Zoom);
        g.ScaleTransform(Zoom, Zoom);

        using var roomFill = new SolidBrush(Themes.Current.RoomFill);
        using var roomPen = new Pen(Themes.Current.RoomBorder, 1 / Zoom);
        using var gamePen = new Pen(Color.FromArgb(255, 200, 60), 3 / Zoom);
        using var nodePen = new Pen(Color.FromArgb(160, 255, 255, 255), 1 / Zoom) { DashStyle = DashStyle.Dot };
        using var selPen = new Pen(Color.White, 2 / Zoom) { DashStyle = DashStyle.Dash };

        if (ShowFillers && Fillers.Count > 0)
        {
            using var fillBrush = new HatchBrush(HatchStyle.BackwardDiagonal, Color.FromArgb(110, 150, 150, 160), Color.FromArgb(60, 90, 90, 100));
            using var fillPen = new Pen(Color.FromArgb(170, 170, 170, 180), 1 / Zoom);
            foreach (var f in Fillers)
            {
                if (!view.IntersectsWith(f)) continue;
                g.FillRectangle(fillBrush, f);
                g.DrawRectangle(fillPen, f);
            }
        }

        int level = MipLevel();
        foreach (var room in Rooms)
        {
            var b = room.Bounds;
            if (!view.IntersectsWith(b)) continue;
            g.FillRectangle(roomFill, b);
            DrawTiles(g, room, b, level);
            g.DrawRectangle(room.Name == GameRoom ? gamePen : roomPen, b);
        }

        foreach (var room in Rooms)
        {
            var vb = room.Bounds; vb.Inflate(64, 64);
            if (!view.IntersectsWith(vb)) continue;
            if (ShowTriggers)
                foreach (var t in room.TriggersEl.Children) DrawTrigger(g, room, t, nodePen);
            if (ShowEntities)
                foreach (var e in room.EntitiesEl.Children) DrawEntity(g, room, e, nodePen);
        }

        DrawDecalOutlines(g, view);
        DrawToolOverlay(g);
        DrawEditOverlay(g);

        if (Selected is { El: null } s)
        {
            using var roomSel = new Pen(Themes.Current.RoomSelect, 3 / Zoom);
            g.DrawRectangle(roomSel, s.Room.Bounds);
        }
        else
        {
            using var otherPen = new Pen(Color.FromArgb(200, 255, 220, 120), 1.5f / Zoom) { DashStyle = DashStyle.Dash };
            foreach (var item in SelectedItems)
            {
                var r = BoxOf(item);
                g.DrawRectangle(item == Selected ? selPen : otherPen, r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2);
            }
            if (SelectedItems.Count == 1 && Selected is { El: not null, IsNode: false, Decal: false } one) DrawResizeHandles(g, one);
        }

        if (PlayerWorld is PointF p)
        {
            float rad = Math.Max(6, 6 / Zoom);
            using var pb = new SolidBrush(Color.FromArgb(230, 255, 80, 200));
            g.FillEllipse(pb, p.X - rad, p.Y - rad * 2, rad * 2, rad * 2);
        }

        g.ResetTransform();
        if (ShowLabels) DrawLabels(g, view);
        if (MinimapVisible) DrawMinimap(g, view);
    }

    private void DrawTiles(Graphics g, Room room, Rectangle b, int level)
    {
        EnsureImage(room);
        var img = room.Image;
        if (img != null)
        {
            img.LastFrame = _frame;
            if (ShowBg) DrawLayer(g, RoomImage.Level(img.Bg, level), b);
            DrawLayer(g, RoomImage.Level(img.Fg, level), b);
        }
        else
        {
            if (ShowBg && room.QuickBg is { } qb) g.DrawImage(qb, new Rectangle(b.X, b.Y, qb.Width * 8, qb.Height * 8));
            if (room.QuickFg is { } qf) g.DrawImage(qf, new Rectangle(b.X, b.Y, qf.Width * 8, qf.Height * 8));
        }

        // cells painted since the cached image was rendered
        if (_pendingCells.TryGetValue(room, out var cells) && cells.Count > 0)
        {
            using var br = new SolidBrush(Color.Black);
            foreach (var (cell, c) in cells)
            {
                br.Color = c == '0' ? Color.FromArgb(44, 44, 54) : Color.FromArgb(Autotiler.FlatColor(c, !PaintFg));
                g.FillRectangle(br, cell.X * 8, cell.Y * 8, 8, 8);
            }
        }
    }

    private static void DrawLayer(Graphics g, Bitmap? bmp, Rectangle b)
    {
        if (bmp == null) return;
        int w = Math.Max(8, b.Width), h = Math.Max(8, b.Height);
        g.DrawImage(bmp, new Rectangle(b.X, b.Y, w, h), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
    }

    private void DrawTrigger(Graphics g, Room room, BinElement t, Pen nodePen)
    {
        var r = MapModel.Box(room, t, true);
        var def = PluginCatalog.Trigger(t.Name);
        var fill = def?.Fill ?? Color.FromArgb(60, 120, 170, 255);
        var border = def?.Border ?? Color.FromArgb(200, 120, 170, 255);
        using var b = new SolidBrush(Color.FromArgb(Math.Min((int)fill.A, 90), fill));
        using var p = new Pen(border, 1 / Zoom);
        g.FillRectangle(b, r);
        g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
        DrawNodes(g, room, t, r, nodePen);
    }

    private void DrawEntity(Graphics g, Room room, BinElement e, Pen nodePen)
    {
        var r = MapModel.Box(room, e, false);
        bool drawn = false;

        if (EntityVisuals.TileChar(e) is char c && _fgTiler != null && _fgTiler.Has(c))
        {
            if (EntityVisuals.Block(_fgTiler, c, (int)r.Width, (int)r.Height) is { } block)
            {
                g.DrawImage(block, r.X, r.Y, r.Width, r.Height);
                drawn = true;
            }
        }
        else if (EntityVisuals.SpikeDir(e.Name) is { } dir)
            drawn = DrawSpikes(g, e, r, dir);
        else if (EntityVisuals.SpriteFor(e) is { } sp && EntityVisuals.Sprite(sp.Path) is { } tex)
        {
            bool sized = e.Int("width") > 0 && e.Int("height") > 0;
            float x = room.X + e.Num("x"), y = room.Y + e.Num("y");
            if (sized) { x = r.X + r.Width * sp.Justify.X; y = r.Y + r.Height * sp.Justify.Y; }
            g.DrawImage(tex.Bitmap, x - tex.W * sp.Justify.X, y - tex.H * sp.Justify.Y, tex.W, tex.H);
            if (sized)
            {
                using var p = new Pen(Color.FromArgb(160, 255, 150, 150), 1 / Zoom);
                g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            }
            drawn = true;
        }

        if (!drawn)
        {
            var def = PluginCatalog.Entity(e.Name);
            bool spawn = MapModel.IsSpawn(e);
            var fill = spawn ? Color.FromArgb(220, 80, 230, 120) : def?.Fill ?? Color.FromArgb(120, 220, 80, 80);
            var border = def?.Border ?? Color.FromArgb(230, 255, 120, 120);
            using var b = new SolidBrush(fill);
            g.FillRectangle(b, r);
            if (!spawn)
            {
                using var p = new Pen(border, 1 / Zoom);
                g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            }
        }
        DrawNodes(g, room, e, r, nodePen);
    }

    private static bool DrawSpikes(Graphics g, BinElement e, RectangleF r, string dir)
    {
        string type = e.Str("type", "default");
        if (type.Length == 0) type = "default";
        string d = dir.ToLowerInvariant();
        var tex = EntityVisuals.Sprite($"danger/spikes/{type}_{d}00") ?? EntityVisuals.Sprite($"danger/spikes/default_{d}00");
        if (tex == null) return false;
        bool horizontal = dir is "Up" or "Down";
        int count = Math.Max(1, (int)((horizontal ? r.Width : r.Height) / 8));
        for (int i = 0; i < count; i++)
        {
            float x = r.X, y = r.Y;
            switch (dir)
            {
                case "Up": x += i * 8; y = r.Bottom - tex.H; break;
                case "Down": x += i * 8; break;
                case "Left": y += i * 8; x = r.Right - tex.W; break;
                case "Right": y += i * 8; break;
            }
            g.DrawImage(tex.Bitmap, x, y, tex.W, tex.H);
        }
        return true;
    }

    private static void DrawNodes(Graphics g, Room room, BinElement e, RectangleF box, Pen pen)
    {
        var center = new PointF(box.X + box.Width / 2, box.Y + box.Height / 2);
        foreach (var n in e.Children.Where(c => c.Name == "node"))
        {
            var np = new PointF(room.X + n.Num("x"), room.Y + n.Num("y"));
            g.DrawLine(pen, center, np);
            g.DrawEllipse(pen, np.X - 3, np.Y - 3, 6, 6);
            center = np;
        }
    }

    private void DrawToolOverlay(Graphics g)
    {
        bool roomDrag = (_drag is Drag.RoomMove or Drag.RoomResize && _dragMoved) || _drag == Drag.RoomCreate;
        if (roomDrag)
        {
            using var b = new SolidBrush(Color.FromArgb(50, 120, 200, 255));
            using var p = new Pen(Color.FromArgb(220, 120, 200, 255), 2 / Zoom) { DashStyle = DashStyle.Dash };
            g.FillRectangle(b, _roomPreview);
            g.DrawRectangle(p, _roomPreview);
            return;
        }
        if (_hoverCell is not { } cell) return;
        if (_tool == Tool.Rect && _drag == Drag.RectPaint)
        {
            var rect = Rectangle.FromLTRB(Math.Min(_rectStart.X, cell.X) * 8, Math.Min(_rectStart.Y, cell.Y) * 8,
                                          (Math.Max(_rectStart.X, cell.X) + 1) * 8, (Math.Max(_rectStart.Y, cell.Y) + 1) * 8);
            using var b = new SolidBrush(Color.FromArgb(70, _strokeChar == '0' ? Color.Red : Color.White));
            using var p = new Pen(Color.White, 1 / Zoom);
            g.FillRectangle(b, rect);
            g.DrawRectangle(p, rect);
        }
        else if (_tool is Tool.Brush or Tool.Rect or Tool.Fill)
        {
            using var p = new Pen(PaintFg ? Color.White : Color.LightSkyBlue, Math.Max(1, 1.5f / Zoom));
            g.DrawRectangle(p, cell.X * 8, cell.Y * 8, 8, 8);
        }
        else if (_tool == Tool.Place && PlaceItem != null)
        {
            using var p = new Pen(Color.FromArgb(200, 255, 255, 120), 1 / Zoom) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(p, cell.X * 8, cell.Y * 8, 8, 8);
        }
    }

    private void DrawLabels(Graphics g, RectangleF view)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        foreach (var room in Rooms)
        {
            if (!view.IntersectsWith(room.Bounds) || room.W * Zoom < 40) continue;
            var sp = ToScreen(new PointF(room.X, room.Y));
            g.FillRectangle(shadow, sp.X + 1, sp.Y + 1, g.MeasureString(room.Name, LabelFont).Width, 15);
            g.DrawString(room.Name, LabelFont, room.Name == GameRoom ? Brushes.Gold : Brushes.Gainsboro, sp.X + 1, sp.Y);
        }
        if (Zoom < 2f) return;
        foreach (var room in Rooms)
        {
            if (!view.IntersectsWith(room.Bounds)) continue;
            if (ShowEntities)
                foreach (var e in room.EntitiesEl.Children)
                {
                    if (EntityVisuals.SpriteFor(e) is { } s && EntityVisuals.Sprite(s.Path) != null) continue;
                    if (EntityVisuals.TileChar(e) != null) continue;
                    var r = MapModel.Box(room, e, false);
                    var sp = ToScreen(new PointF(r.X, r.Bottom));
                    g.DrawString(e.Name, SmallFont, Brushes.White, sp.X, sp.Y);
                }
            if (ShowTriggers)
                foreach (var t in room.TriggersEl.Children)
                {
                    var r = MapModel.Box(room, t, true);
                    var sp = ToScreen(new PointF(r.X, r.Y));
                    g.DrawString(t.Name, SmallFont, Brushes.LightSkyBlue, sp.X + 1, sp.Y + 1);
                }
        }
    }

    // ---- minimap ----

    private (float Scale, PointF Offset) MinimapTransform()
    {
        var mb = MapBounds();
        var mr = MinimapRect;
        float scale = Math.Min((mr.Width - 8f) / Math.Max(1, mb.Width), (mr.Height - 8f) / Math.Max(1, mb.Height));
        var off = new PointF(mr.X + (mr.Width - mb.Width * scale) / 2 - mb.X * scale,
                             mr.Y + (mr.Height - mb.Height * scale) / 2 - mb.Y * scale);
        return (scale, off);
    }

    private void DrawMinimap(Graphics g, RectangleF view)
    {
        var mr = MinimapRect;
        using var bg = new SolidBrush(Color.FromArgb(200, Themes.Blend(Themes.Current.Canvas, Color.Black, 0.4f)));
        g.FillRectangle(bg, mr);
        using (var mb = new Pen(Themes.Current.RoomBorder)) g.DrawRectangle(mb, mr);
        var (scale, off) = MinimapTransform();
        using var roomB = new SolidBrush(Themes.Current.RoomBorder);
        using var gameB = new SolidBrush(Color.FromArgb(255, 200, 60));
        using var selB = new SolidBrush(Themes.Current.RoomSelect);
        foreach (var r in Rooms)
        {
            var b = r.Bounds;
            var br = Selected?.Room == r ? selB : r.Name == GameRoom ? gameB : roomB;
            g.FillRectangle(br, off.X + b.X * scale, off.Y + b.Y * scale, Math.Max(1, b.Width * scale), Math.Max(1, b.Height * scale));
        }
        var vr = new RectangleF(off.X + view.X * scale, off.Y + view.Y * scale, view.Width * scale, view.Height * scale);
        vr.Intersect(mr);
        if (!vr.IsEmpty) g.DrawRectangle(Pens.White, vr.X, vr.Y, Math.Max(2, vr.Width), Math.Max(2, vr.Height));
    }

    private void MinimapNavigate(Point screen)
    {
        var (scale, off) = MinimapTransform();
        CenterOn(new PointF((screen.X - off.X) / scale, (screen.Y - off.Y) / scale));
    }
}
