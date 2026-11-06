using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CELMapViewer;

/// <summary>Rysy/Lönn-style item editing on the canvas: layers, decals, nodes, multi-select, resize handles, previews.</summary>
public sealed partial class MapCanvas
{
    private static readonly ConcurrentDictionary<string, Size> DecalSizes = new(StringComparer.OrdinalIgnoreCase);
    private (PlacementDef Def, BinElement El)? _ghost;

    public static IEnumerable<BinElement> Nodes(BinElement el) => el.Children.Where(c => c.Name == "node");

    private bool LayerHas(EditLayer l) => _layer == l || (_layer == EditLayer.All && l is EditLayer.Entities or EditLayer.Triggers);

    /// <summary>Selectable items of a room on the active layer, in draw order (topmost last).</summary>
    public List<Selection> ItemsOf(Room room)
    {
        var list = new List<Selection>();
        if (LayerHas(EditLayer.BgDecals) && _showBgDecals)
            foreach (var d in room.El.Child("bgdecals")?.Children ?? new()) list.Add(new Selection(room, d, false, true));
        if (LayerHas(EditLayer.FgDecals) && _showFgDecals)
            foreach (var d in room.El.Child("fgdecals")?.Children ?? new()) list.Add(new Selection(room, d, false, true));
        if (LayerHas(EditLayer.Triggers) && ShowTriggers)
            foreach (var t in room.TriggersEl.Children) list.Add(new Selection(room, t, true));
        if (LayerHas(EditLayer.Entities) && ShowEntities)
            foreach (var e in room.EntitiesEl.Children) list.Add(new Selection(room, e, false));
        return list;
    }

    /// <summary>Items on the active layer whose boxes intersect a world rectangle.</summary>
    public List<Selection> ItemsInRect(RectangleF r)
    {
        var list = new List<Selection>();
        foreach (var room in Rooms)
        {
            var rb = room.Bounds; rb.Inflate(64, 64);
            if (!r.IntersectsWith(rb)) continue;
            list.AddRange(ItemsOf(room).Where(s => r.IntersectsWith(BoxOf(s))));
        }
        return list;
    }

    public static RectangleF BoxOf(Selection s)
    {
        if (s.El == null) return s.Room.Bounds;
        if (s.NodeEl is { } n) return new RectangleF(s.Room.X + n.Num("x") - 4, s.Room.Y + n.Num("y") - 4, 8, 8);
        if (s.Decal) return DecalBox(s.Room, s.El);
        return MapModel.Box(s.Room, s.El, s.Trigger);
    }

    public static RectangleF DecalBox(Room room, BinElement d)
    {
        var size = DecalSize(d.Str("texture"));
        float sx = d.Num("scaleX", 1), sy = d.Num("scaleY", 1);
        float w = size.Width * MathF.Abs(sx == 0 ? 1 : sx), h = size.Height * MathF.Abs(sy == 0 ? 1 : sy);
        return new RectangleF(room.X + d.Num("x") - w / 2, room.Y + d.Num("y") - h / 2, w, h);
    }

    private static Size DecalSize(string texture)
    {
        if (DecalSizes.TryGetValue(texture, out var s)) return s;
        if (RoomRenderJob.DecalTexture(texture) is not { } tex) return new Size(8, 8);
        return DecalSizes[texture] = new Size(tex.W, tex.H);
    }

    // ---- item resize ----

    /// <summary>Which edges of a sized entity/trigger the screen point is on (1 left, 2 top, 4 right, 8 bottom).</summary>
    private int ItemEdgesAt(Selection s, Point screen)
    {
        if (s.El == null) return 0;
        bool hw = s.El.Find("width") != null, hh = s.El.Find("height") != null;
        if (!hw && !hh) return 0;
        var b = BoxOf(s);
        var tl = ToScreen(b.Location);
        var br = ToScreen(new PointF(b.Right, b.Bottom));
        const float m = 5;
        if (screen.X < tl.X - m || screen.X > br.X + m || screen.Y < tl.Y - m || screen.Y > br.Y + m) return 0;
        int edges = 0;
        if (hw && br.X - tl.X >= 3 * m)
        {
            if (Math.Abs(screen.X - tl.X) <= m) edges |= 1;
            else if (Math.Abs(screen.X - br.X) <= m) edges |= 4;
        }
        if (hh && br.Y - tl.Y >= 3 * m)
        {
            if (Math.Abs(screen.Y - tl.Y) <= m) edges |= 2;
            else if (Math.Abs(screen.Y - br.Y) <= m) edges |= 8;
        }
        return edges;
    }

    private void BeginItemResize(Selection s, int edges)
    {
        var el = s.El!;
        _resizeItem = s;
        _resizeEdges = edges;
        _resizeOrig = new RectangleF(el.Num("x"), el.Num("y"), el.Num("width"), el.Num("height"));
        _placeResize = false;
        _drag = Drag.ItemResize;
    }

    private void ApplyItemResize(PointF w)
    {
        var el = _resizeItem!.El!;
        var room = _resizeItem.Room;
        bool free = (ModifierKeys & Keys.Alt) != 0;
        float min = free ? 1 : 8;
        float Snap(float v) => free ? MathF.Round(v) : MathF.Round(v / 8) * 8;
        var o = _resizeOrig;
        if (_placeResize)
        {
            float lx = w.X - room.X, ly = w.Y - room.Y;
            float Ceil(float v) => free ? MathF.Ceiling(v) : MathF.Ceiling(v / 8) * 8;
            if ((_resizeEdges & 4) != 0) SetNum(el, "width", Math.Max(min, Ceil(lx - o.X)));
            if ((_resizeEdges & 8) != 0) SetNum(el, "height", Math.Max(min, Ceil(ly - o.Y)));
            return;
        }
        float dx = Snap(w.X - _dragStartWorld.X), dy = Snap(w.Y - _dragStartWorld.Y);
        if ((_resizeEdges & 1) != 0) { float nw = Math.Max(min, o.Width - dx); SetNum(el, "x", o.X + o.Width - nw); SetNum(el, "width", nw); }
        if ((_resizeEdges & 4) != 0) SetNum(el, "width", Math.Max(min, o.Width + dx));
        if ((_resizeEdges & 2) != 0) { float nh = Math.Max(min, o.Height - dy); SetNum(el, "y", o.Y + o.Height - nh); SetNum(el, "height", nh); }
        if ((_resizeEdges & 8) != 0) SetNum(el, "height", Math.Max(min, o.Height + dy));
    }

    private void DrawResizeHandles(Graphics g, Selection s)
    {
        if (s.El == null) return;
        bool hw = s.El.Find("width") != null, hh = s.El.Find("height") != null;
        if (!hw && !hh) return;
        var b = BoxOf(s);
        float hs = 4 / Zoom;
        using var br = new SolidBrush(Color.White);
        void H(float x, float y) => g.FillRectangle(br, x - hs / 2, y - hs / 2, hs, hs);
        if (hw) { H(b.Left, b.Top + b.Height / 2); H(b.Right, b.Top + b.Height / 2); }
        if (hh) { H(b.Left + b.Width / 2, b.Top); H(b.Left + b.Width / 2, b.Bottom); }
    }

    // ---- decals ----

    private void PlaceDecalAt(PointF w)
    {
        if (string.IsNullOrEmpty(PlaceDecal) || RoomAt(w) is not { } room) return;
        BeforeEdit?.Invoke();
        var el = NewDecal(PlaceDecal, MathF.Round(w.X - room.X), MathF.Round(w.Y - room.Y));
        room.GetOrAdd(PaintFg ? "fgdecals" : "bgdecals").Children.Add(el);
        room.InvalidateRender();
        Select(new Selection(room, el, false, true));
        Edited?.Invoke();
    }

    public static BinElement NewDecal(string name, float x, float y)
    {
        var el = new BinElement("decal");
        SetNum(el, "x", x);
        SetNum(el, "y", y);
        el.Set("scaleX", 1f);
        el.Set("scaleY", 1f);
        el.Set("texture", name.Replace('\\', '/').Trim('/') + ".png");
        return el;
    }

    private void DrawDecalOutlines(Graphics g, RectangleF view)
    {
        if (_layer is not (EditLayer.FgDecals or EditLayer.BgDecals) || Zoom < 0.2f) return;
        bool fg = _layer == EditLayer.FgDecals;
        if (fg ? !_showFgDecals : !_showBgDecals) return;
        using var p = new Pen(fg ? Color.FromArgb(150, 255, 200, 90) : Color.FromArgb(150, 120, 200, 255), 1 / Zoom);
        foreach (var room in Rooms)
        {
            if (!view.IntersectsWith(room.Bounds)) continue;
            foreach (var d in room.El.Child(fg ? "fgdecals" : "bgdecals")?.Children ?? new())
            {
                var b = DecalBox(room, d);
                g.DrawRectangle(p, b.X, b.Y, b.Width, b.Height);
            }
        }
    }

    // ---- overlays ----

    private void DrawEditOverlay(Graphics g)
    {
        if (_drag == Drag.BoxSelect && _dragMoved && !_boxRect.IsEmpty)
        {
            using var b = new SolidBrush(Color.FromArgb(40, 120, 200, 255));
            using var p = new Pen(Color.FromArgb(220, 120, 200, 255), 1 / Zoom);
            g.FillRectangle(b, _boxRect);
            g.DrawRectangle(p, _boxRect.X, _boxRect.Y, _boxRect.Width, _boxRect.Height);
        }
        if (_drag == Drag.LinePaint && _hoverCell is { } end)
        {
            using var b = new SolidBrush(Color.FromArgb(110, _strokeChar == '0' ? Color.Red : Color.White));
            foreach (var c in LineCells(_rectStart, end)) g.FillRectangle(b, c.X * 8, c.Y * 8, 8, 8);
        }
        if (_drag != Drag.None || _hoverWorld is not { } hw || RoomAt(hw) is not { } room) return;
        if (_tool == Tool.Place && PlaceItem != null) DrawPlaceGhost(g, room, hw);
        else if (_tool == Tool.Decal && !string.IsNullOrEmpty(PlaceDecal) && RoomRenderJob.DecalTexture(PlaceDecal) is { } tex)
        {
            using var ia = new ImageAttributes();
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = 0.6f });
            float x = MathF.Round(hw.X) - tex.W / 2f, y = MathF.Round(hw.Y) - tex.H / 2f;
            g.DrawImage(tex.Bitmap, new Rectangle((int)MathF.Round(x), (int)MathF.Round(y), tex.W, tex.H), 0, 0, tex.W, tex.H, GraphicsUnit.Pixel, ia);
        }
    }

    private void DrawPlaceGhost(Graphics g, Room room, PointF w)
    {
        var def = PlaceItem!;
        if (_ghost?.Def != def) _ghost = (def, def.Create());
        var el = _ghost.Value.El;
        bool trigger = def.Plugin.Trigger;
        float x = w.X - room.X, y = w.Y - room.Y;
        if (trigger || el.Int("width") > 0 || el.Int("height") > 0) { x = MathF.Floor(x / 8) * 8; y = MathF.Floor(y / 8) * 8; }
        else if ((ModifierKeys & Keys.Alt) == 0) { x = MapModel.Snap8(x); y = MapModel.Snap8(y); }
        SetNum(el, "x", MathF.Round(x));
        SetNum(el, "y", MathF.Round(y));
        using var nodePen = new Pen(Color.FromArgb(120, 255, 255, 255), 1 / Zoom) { DashStyle = DashStyle.Dot };
        var state = g.Save();
        try
        {
            if (trigger) DrawTrigger(g, room, el, nodePen);
            else DrawEntity(g, room, el, nodePen);
        }
        catch { /* a half-configured placement must not break painting */ }
        g.Restore(state);
        var r = MapModel.Box(room, el, trigger);
        using var p = new Pen(Color.FromArgb(200, 255, 255, 120), 1 / Zoom) { DashStyle = DashStyle.Dot };
        g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
    }

    // ---- keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && !_spaceDown && _drag == Drag.None)
        {
            _spaceDown = true;
            Cursor = Cursors.SizeAll;
            e.Handled = e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { _spaceDown = false; Cursor = Cursors.Default; }
        base.OnKeyUp(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _spaceDown = false;
        base.OnLostFocus(e);
    }
}
