using System.Drawing.Imaging;

namespace CELMapViewer;

/// <summary>A room ("level" element) with its editable tile grids and cached rendering.</summary>
public sealed class Room
{
    public readonly BinElement El;
    private Bitmap? _fg, _bg;
    private TileGrid? _fgGrid, _bgGrid;

    /// <summary>Bumped whenever the rendered look changes; the canvas re-renders when its image is older.</summary>
    public int RenderVersion { get; private set; }
    internal int RequestedVersion = -1;
    internal RoomImage? Image;

    public Room(BinElement el) { El = el; }

    public int Cols => Math.Max(1, (W + 7) / 8);
    public int Rows => Math.Max(1, (H + 7) / 8);

    /// <summary>The fg ("solids") or bg tile grid, parsed once; edits go through <see cref="CommitGrid"/>.</summary>
    public TileGrid Grid(bool fg)
    {
        ref TileGrid? g = ref fg ? ref _fgGrid : ref _bgGrid;
        if (g == null || g.Cols != Cols || g.Rows != Rows)
            g = TileGrid.Parse(El.Child(fg ? "solids" : "bg")?.Str("innerText") ?? "", Cols, Rows);
        return g;
    }

    /// <summary>Writes a grid back into the map element.</summary>
    public void CommitGrid(bool fg)
    {
        var el = El.Child(fg ? "solids" : "bg");
        if (el == null)
        {
            El.Children.Add(el = new BinElement(fg ? "solids" : "bg"));
            el.Set("offsetX", 0);
            el.Set("offsetY", 0);
        }
        string text = Grid(fg).Serialize();
        if (el.Find("innerText") is { } a) a.Value = text;
        else el.Attrs.Add(new BinAttr("innerText", BinType.Rle, text));
        InvalidateRender();
    }

    /// <summary>Re-reads the tile grids from the map element (after it was edited directly).</summary>
    public void ReloadTiles()
    {
        _fgGrid = _bgGrid = null;
        InvalidateRender();
    }

    public void InvalidateRender()
    {
        RenderVersion++;
        _fg?.Dispose(); _bg?.Dispose();
        _fg = _bg = null;
    }

    public void DisposeImage()
    {
        Image?.Dispose();
        Image = null;
        RequestedVersion = -1;
    }

    public string RawName => El.Str("name");
    public string Name => RawName.StartsWith("lvl_") ? RawName[4..] : RawName;
    public int X => El.Int("x");
    public int Y => El.Int("y");
    public int W => El.Int("width");
    public int H => El.Int("height");
    public Rectangle Bounds => new(X, Y, W, H);

    public BinElement EntitiesEl => GetOrAdd("entities");
    public BinElement TriggersEl => GetOrAdd("triggers");

    public BinElement GetOrAdd(string name)
    {
        var c = El.Child(name);
        if (c == null) El.Children.Add(c = new BinElement(name));
        return c;
    }

    /// <summary>Quick placeholders (one pixel per tile) shown until the autotiled image is ready.</summary>
    public Bitmap? QuickFg => _fg ??= TileBitmap(true);
    public Bitmap? QuickBg => _bg ??= TileBitmap(false);

    private Bitmap? TileBitmap(bool fg)
    {
        var g = Grid(fg);
        var pixels = new int[g.Cols * g.Rows];
        bool any = false;
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
                if (g[x, y] != '0') { pixels[y * g.Cols + x] = Autotiler.FlatColor(g[x, y], !fg); any = true; }
        return any ? Tex.ToBitmap(pixels, g.Cols, g.Rows) : null;
    }
}

public static class MapModel
{
    public static BinElement Levels(MapBin map)
    {
        var levels = map.Root.Child("levels");
        if (levels == null) map.Root.Children.Add(levels = new BinElement("levels"));
        return levels;
    }

    public static int NextId(MapBin map)
    {
        int max = 0;
        foreach (var level in Levels(map).Children)
            foreach (string list in new[] { "entities", "triggers" })
                foreach (var e in level.Child(list)?.Children ?? new())
                    max = Math.Max(max, e.Int("id"));
        return max + 1;
    }

    public static bool IsSpawn(BinElement e) => e.Name == "player";

    public static Rectangle MapBounds(MapBin map)
    {
        Rectangle? r = null;
        foreach (var l in Levels(map).Children)
        {
            var b = new Rectangle(l.Int("x"), l.Int("y"), l.Int("width"), l.Int("height"));
            r = r == null ? b : Rectangle.Union(r.Value, b);
        }
        return r ?? Rectangle.Empty;
    }

    /// <summary>
    /// Adds generated rooms to the map: unique prefixed names, placed to the right of the
    /// existing rooms (or at <paramref name="at"/>), entity ids continuing the map's.
    /// Mirrors encoder.lua's levelElement. Returns the new room elements.
    /// </summary>
    public static List<BinElement> ImportPcg(MapBin map, PcgResult gen, string prefix, Point? at = null)
    {
        var levels = Levels(map);
        bool lvlPrefix = levels.Children.Count == 0 || levels.Children.Any(l => l.Str("name").StartsWith("lvl_"));
        var names = new HashSet<string>(levels.Children.Select(l => l.Str("name")));

        int minX = gen.Rooms.Min(r => r.X), minY = gen.Rooms.Min(r => r.Y);
        Point origin;
        if (at != null) origin = at.Value;
        else
        {
            var b = MapBounds(map);
            origin = b.IsEmpty ? Point.Empty : new Point(b.Right + 64, b.Top);
        }
        int dx = Snap8(origin.X - minX), dy = Snap8(origin.Y - minY);

        int id = NextId(map);
        var added = new List<BinElement>();
        foreach (var r in gen.Rooms)
        {
            string name = (lvlPrefix ? "lvl_" : "") + prefix + r.Name;
            for (int n = 2; names.Contains(name); n++) name = (lvlPrefix ? "lvl_" : "") + prefix + r.Name + "_" + n;
            names.Add(name);

            string bg = r.Bg != null ? string.Join("\n", r.Bg)
                : string.Join("\n", Enumerable.Repeat(new string('0', r.WTiles), r.HTiles));
            var level = NewLevel(name, r.X + dx, r.Y + dy, r.W, r.H, string.Join("\n", r.Fg), bg);
            var entities = level.Child("entities")!;
            foreach (var e in r.Entities)
            {
                var el = new BinElement(e.Name);
                el.Set("id", id++);
                el.Set("x", e.X);
                el.Set("y", e.Y);
                if (e.W != null) el.Set("width", e.W.Value);
                if (e.H != null) el.Set("height", e.H.Value);
                foreach (var (k, v) in e.Attrs) el.Set(k, v);
                entities.Children.Add(el);
            }
            levels.Children.Add(level);
            added.Add(level);
        }
        return added;
    }

    /// <summary>A new level element with the attributes and children Lönn writes. Mirrors encoder.lua's levelElement.</summary>
    public static BinElement NewLevel(string name, int x, int y, int w, int h, string solids = "", string bg = "")
    {
        var level = new BinElement("level");
        level.Set("name", name);
        level.Set("x", x);
        level.Set("y", y);
        level.Set("width", w);
        level.Set("height", h);
        foreach (var (k, v) in new (string, object)[]
        {
            ("music", ""), ("alt_music", ""), ("ambience", ""),
            ("musicLayer1", true), ("musicLayer2", true), ("musicLayer3", true), ("musicLayer4", true),
            ("musicProgress", ""), ("ambienceProgress", ""),
            ("dark", false), ("underwater", false), ("space", false), ("whisper", false),
            ("disableDownTransition", false), ("windPattern", "None"), ("c", 0),
            ("cameraOffsetX", 0), ("cameraOffsetY", 0)
        })
            level.Set(k, v);
        level.Children.Add(Tiles("solids", solids));
        level.Children.Add(Tiles("bg", bg));
        level.Children.Add(new BinElement("entities"));
        level.Children.Add(new BinElement("triggers"));
        foreach (string decals in new[] { "fgdecals", "bgdecals" })
        {
            var d = new BinElement(decals);
            d.Set("tileset", "0");
            d.Set("offsetX", 0);
            d.Set("offsetY", 0);
            level.Children.Add(d);
        }
        return level;
    }

    /// <summary>A level name not used yet, keeping the map's "lvl_" convention.</summary>
    public static string UniqueName(MapBin map, string wanted)
    {
        var levels = Levels(map).Children;
        bool lvl = levels.Count == 0 || levels.Any(l => l.Str("name").StartsWith("lvl_"));
        string bare = wanted.StartsWith("lvl_") ? wanted[4..] : wanted;
        var names = new HashSet<string>(levels.Select(l => l.Str("name") is var n && n.StartsWith("lvl_") ? n[4..] : n));
        string name = bare;
        for (int i = 2; names.Contains(name); i++) name = bare + "_" + i;
        return (lvl ? "lvl_" : "") + name;
    }

    /// <summary>Copies a level under a new name at a new position, with fresh entity/trigger ids.</summary>
    public static BinElement DuplicateLevel(MapBin map, BinElement level, int x, int y)
    {
        var copy = level.Clone();
        copy.Set("name", UniqueName(map, level.Str("name")));
        copy.Set("x", x);
        copy.Set("y", y);
        int id = NextId(map);
        foreach (string list in new[] { "entities", "triggers" })
            foreach (var e in copy.Child(list)?.Children ?? new())
                e.Set("id", id++);
        Levels(map).Children.Add(copy);
        return copy;
    }

    /// <summary>
    /// Moves/resizes a room to <paramref name="nb"/> (world space, multiples of 8) keeping content in place in the world:
    /// tiles, entities, triggers, nodes and decals shift when the left/top edges move.
    /// </summary>
    public static void ResizeRoom(Room room, Rectangle nb)
    {
        nb.Width = Math.Max(8, nb.Width); nb.Height = Math.Max(8, nb.Height);
        int dx = room.X - nb.X, dy = room.Y - nb.Y;
        int cols = (nb.Width + 7) / 8, rows = (nb.Height + 7) / 8;
        var fg = room.Grid(true).Resized(cols, rows, dx / 8, dy / 8);
        var bg = room.Grid(false).Resized(cols, rows, dx / 8, dy / 8);

        var el = room.El;
        el.Set("x", nb.X); el.Set("y", nb.Y);
        el.Set("width", nb.Width); el.Set("height", nb.Height);
        SetTiles(el, "solids", fg.Serialize());
        SetTiles(el, "bg", bg.Serialize());
        foreach (string csv in new[] { "objtiles", "fgtiles", "bgtiles" })
            if (el.Child(csv) is { } c && c.Find("innerText") is { } a)
                a.Value = ShiftCsv(a.Value?.ToString() ?? "", cols, rows, dx / 8, dy / 8);

        if (dx != 0 || dy != 0)
        {
            foreach (string list in new[] { "entities", "triggers" })
                foreach (var e in el.Child(list)?.Children ?? new())
                    foreach (var p in e.Children.Where(n => n.Name == "node").Prepend(e))
                        Offset(p, dx, dy);
            foreach (string list in new[] { "fgdecals", "bgdecals" })
                foreach (var d in el.Child(list)?.Children ?? new())
                    Offset(d, dx, dy);
        }
        room.ReloadTiles();
    }

    private static void Offset(BinElement e, int dx, int dy)
    {
        MapCanvas.SetNum(e, "x", e.Num("x") + dx);
        MapCanvas.SetNum(e, "y", e.Num("y") + dy);
    }

    private static void SetTiles(BinElement level, string name, string text)
    {
        var c = level.Child(name);
        if (c == null) { level.Children.Add(Tiles(name, text)); return; }
        if (c.Find("innerText") is { } a) a.Value = text;
        else c.Attrs.Add(new BinAttr("innerText", BinType.Rle, text));
    }

    private static string ShiftCsv(string text, int cols, int rows, int dx, int dy)
    {
        var src = text.Replace("\r", "").Split('\n').Select(l => l.Split(',', StringSplitOptions.None)).ToArray();
        var outRows = new List<string>();
        for (int y = 0; y < rows; y++)
        {
            var cells = new string[cols];
            for (int x = 0; x < cols; x++)
            {
                int sx = x - dx, sy = y - dy;
                string v = sy >= 0 && sy < src.Length && sx >= 0 && sx < src[sy].Length ? src[sy][sx].Trim() : "-1";
                cells[x] = v.Length == 0 ? "-1" : v;
            }
            outRows.Add(string.Join(",", cells));
        }
        return string.Join("\n", outRows);
    }

    /// <summary>The mod folder a map belongs to (the parent of its "Maps" folder).</summary>
    public static string? ModDirOf(string mapPath)
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(mapPath))!); d != null; d = d.Parent)
            if (d.Name.Equals("Maps", StringComparison.OrdinalIgnoreCase)) return d.Parent?.FullName;
        return null;
    }

    /// <summary>The map's custom tileset xml (meta ForegroundTiles/BackgroundTiles), or null for vanilla.</summary>
    public static string? TilesetXml(MapBin map, bool fg)
    {
        string key = fg ? "ForegroundTiles" : "BackgroundTiles";
        var meta = map.Root.Child("meta");
        string? v = meta?.Find(key)?.Value as string;
        if (string.IsNullOrWhiteSpace(v))
            foreach (var c in meta?.Children ?? new())
                if (c.Find(key)?.Value is string s && s.Length > 0) { v = s; break; }
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static BinElement Tiles(string name, string text)
    {
        var e = new BinElement(name);
        e.Set("offsetX", 0);
        e.Set("offsetY", 0);
        e.Attrs.Add(new BinAttr("innerText", BinType.Rle, text));
        return e;
    }

    public static int Snap8(float v) => (int)MathF.Round(v / 8f) * 8;

    /// <summary>Entity/trigger hit box in world space, following Lönn's defaults for unsized entities.</summary>
    public static RectangleF Box(Room room, BinElement e, bool trigger)
    {
        float x = room.X + e.Num("x"), y = room.Y + e.Num("y");
        float w = e.Num("width"), h = e.Num("height");
        if (w > 0 && h > 0) return new RectangleF(x, y, w, h);
        if (trigger) return new RectangleF(x, y, 16, 16);
        switch (EntityVisuals.SpikeDir(e.Name))
        {
            case "Up": return new RectangleF(x, y - 8, Math.Max(8, w), 8);
            case "Down": return new RectangleF(x, y, Math.Max(8, w), 8);
            case "Left": return new RectangleF(x - 8, y, 8, Math.Max(8, h));
            case "Right": return new RectangleF(x, y, 8, Math.Max(8, h));
        }
        if (w > 0) return new RectangleF(x, y, w, 8);
        if (h > 0) return new RectangleF(x, y, 8, h);
        if (EntityVisuals.SpriteBounds(room, e) is { } sb) return sb;
        if (IsSpawn(e)) return new RectangleF(x - 4, y - 11, 8, 11);
        return new RectangleF(x - 4, y - 4, 8, 8);
    }
}
