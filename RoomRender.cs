using System.Collections.Concurrent;

namespace CELMapViewer;

/// <summary>Background workers for texture loading and room rendering. Jobs with the same key are coalesced.</summary>
public static class AssetWorker
{
    private static readonly object Gate = new();
    private static readonly LinkedList<object> High = new(), Low = new();
    private static readonly Dictionary<object, Action> Jobs = new();
    private static int _threads;

    /// <summary>Raised on a worker thread after each job.</summary>
    public static event Action? Completed;

    public static int Pending { get { lock (Gate) return Jobs.Count; } }

    public static void Enqueue(object key, Action work, bool high = false)
    {
        lock (Gate)
        {
            bool queued = Jobs.ContainsKey(key);
            Jobs[key] = work;
            if (!queued) (high ? High : Low).AddLast(key);
            if (_threads == 0)
            {
                _threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
                for (int i = 0; i < _threads; i++)
                    new Thread(Loop) { IsBackground = true, Name = "AssetWorker" + i, Priority = ThreadPriority.BelowNormal }.Start();
            }
            Monitor.Pulse(Gate);
        }
    }

    private static void Loop()
    {
        while (true)
        {
            Action job;
            lock (Gate)
            {
                while (High.Count == 0 && Low.Count == 0) Monitor.Wait(Gate);
                var list = High.Count > 0 ? High : Low;
                object key = list.First!.Value;
                list.RemoveFirst();
                job = Jobs[key];
                Jobs.Remove(key);
            }
            try { job(); } catch { /* a bad asset must not kill the worker */ }
            try { Completed?.Invoke(); } catch { }
        }
    }
}

/// <summary>A room's rendered tile layers (bg tiles + bg decals, fg tiles + fg decals) with mip levels.</summary>
public sealed class RoomImage : IDisposable
{
    public int Version;
    public Bitmap?[]? Bg, Fg;
    public long Pixels;
    public long LastFrame;

    public static Bitmap? Level(Bitmap?[]? mips, int level) =>
        mips == null || mips.Length == 0 ? null : mips[Math.Min(level, mips.Length - 1)];

    public void Dispose()
    {
        foreach (var b in (Bg ?? Array.Empty<Bitmap?>()).Concat(Fg ?? Array.Empty<Bitmap?>())) b?.Dispose();
        Bg = Fg = null;
    }
}

public sealed record DecalSnap(float X, float Y, float ScaleX, float ScaleY, string Texture);

/// <summary>Everything needed to render a room, copied on the UI thread so the worker never touches the live map.</summary>
public sealed class RoomRenderJob
{
    public required int W, H, Version;
    public TileGrid? Fg, Bg;
    public Autotiler? FgTiler, BgTiler;
    public List<DecalSnap> FgDecals = new(), BgDecals = new();

    public static RoomRenderJob From(Room r, Autotiler? fg, Autotiler? bg, bool fgDecals = true, bool bgDecals = true) => new()
    {
        W = Math.Max(8, r.W), H = Math.Max(8, r.H), Version = r.RenderVersion,
        Fg = r.Grid(true).Clone(), Bg = r.Grid(false).Clone(), FgTiler = fg, BgTiler = bg,
        FgDecals = fgDecals ? Decals(r.El.Child("fgdecals")) : new(),
        BgDecals = bgDecals ? Decals(r.El.Child("bgdecals")) : new()
    };

    private static List<DecalSnap> Decals(BinElement? list) =>
        list?.Children.Where(d => d.Name == "decal")
            .Select(d => new DecalSnap(d.Num("x"), d.Num("y"), d.Num("scaleX", 1), d.Num("scaleY", 1), d.Str("texture")))
            .ToList() ?? new();

    public RoomImage Render()
    {
        var img = new RoomImage { Version = Version };
        img.Bg = Layer(Bg, BgTiler, BgDecals, out long pb);
        img.Fg = Layer(Fg, FgTiler, FgDecals, out long pf);
        img.Pixels = pb + pf;
        return img;
    }

    private Bitmap?[]? Layer(TileGrid? grid, Autotiler? tiler, List<DecalSnap> decals, out long pixels)
    {
        pixels = 0;
        var px = new int[W * H];
        bool any = false;
        if (grid != null && Enumerable.Range(0, grid.Rows).Any(y => Enumerable.Range(0, grid.Cols).Any(x => grid[x, y] != '0')))
        {
            any = true;
            if (tiler != null) tiler.Render(grid, px, W, H, 0, 0, edgesExtend: true);
            else FlatRender(grid, px, W, H);
        }
        foreach (var d in decals)
        {
            var tex = DecalTexture(d.Texture);
            if (tex == null) continue;
            any = true;
            float sx = d.ScaleX == 0 ? 1 : d.ScaleX, sy = d.ScaleY == 0 ? 1 : d.ScaleY;
            int dx = (int)MathF.Round(d.X - tex.W * MathF.Abs(sx) / 2), dy = (int)MathF.Round(d.Y - tex.H * MathF.Abs(sy) / 2);
            tex.BlitTo(px, W, H, 0, 0, tex.W, tex.H, dx, dy, sx, sy);
        }
        if (!any) return null;
        return Mips(px, W, H, ref pixels);
    }

    public static Tex? DecalTexture(string texture)
    {
        string key = texture.Replace('\\', '/').Trim('/');
        if (key.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) key = key[..^4];
        return GameAssets.Gameplay.GetFirstFrame("decals/" + key);
    }

    private static void FlatRender(TileGrid g, int[] px, int w, int h)
    {
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                char c = g[x, y];
                if (c == '0') continue;
                int col = Autotiler.FlatColor(c, false);
                for (int yy = y * 8; yy < Math.Min(h, y * 8 + 8); yy++)
                    for (int xx = x * 8; xx < Math.Min(w, x * 8 + 8); xx++)
                        px[yy * w + xx] = col;
            }
    }

    /// <summary>Full size plus up to three 2x box-filtered levels (premultiplied, so a plain average is correct).</summary>
    private static Bitmap?[] Mips(int[] px, int w, int h, ref long pixels)
    {
        var list = new List<Bitmap?> { Tex.ToBitmap(px, w, h) };
        pixels += (long)w * h;
        while (list.Count < 4 && w >= 16 && h >= 16)
        {
            int nw = w / 2, nh = h / 2;
            var n = new int[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int a = px[(2 * y) * w + 2 * x], b = px[(2 * y) * w + 2 * x + 1], c = px[(2 * y + 1) * w + 2 * x], d = px[(2 * y + 1) * w + 2 * x + 1];
                    if ((a | b | c | d) == 0) continue;
                    int Ch(int s) => ((((a >> s) & 255) + ((b >> s) & 255) + ((c >> s) & 255) + ((d >> s) & 255) + 2) / 4) << s;
                    n[y * nw + x] = (int)((uint)Ch(24) & 0xFF000000u) | Ch(16) | Ch(8) | Ch(0);
                }
            px = n; w = nw; h = nh;
            list.Add(Tex.ToBitmap(px, w, h));
            pixels += (long)w * h;
        }
        return list.ToArray();
    }
}

/// <summary>Sprites and tile blocks used to draw entities, loaded on the asset workers (null until ready).</summary>
public static class EntityVisuals
{
    private static readonly ConcurrentDictionary<string, Tex?> Sprites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Bitmap?> Blocks = new();

    // Bitmaps are not disposed: a render job on the worker may still be drawing them.
    public static void ClearCache()
    {
        Sprites.Clear();
        Blocks.Clear();
    }

    public static Tex? Sprite(string path)
    {
        if (Sprites.TryGetValue(path, out var t)) return t;
        if (Sprites.TryAdd(path, null))
            AssetWorker.Enqueue("sprite:" + path, () =>
            {
                var tex = GameAssets.Gameplay.GetFirstFrame(path);
                _ = tex?.Bitmap;
                Sprites[path] = tex;
            }, high: true);
        return null;
    }

    public static Bitmap? Block(Autotiler tiler, char c, int w, int h)
    {
        if (w <= 0 || h <= 0 || w * h > 4096 * 4096) return null;
        string key = $"{tiler.Source}|{c}|{w}|{h}";
        if (Blocks.TryGetValue(key, out var b)) return b;
        if (Blocks.TryAdd(key, null))
            AssetWorker.Enqueue("block:" + key, () =>
            {
                var grid = new TileGrid((w + 7) / 8, (h + 7) / 8, c);
                var px = new int[w * h];
                tiler.Render(grid, px, w, h, 0, 0, edgesExtend: false);
                Blocks[key] = Tex.ToBitmap(px, w, h);
            }, high: true);
        return null;
    }

    /// <summary>The tile char of a tile-block entity (dashBlock, fakeWall, fallingBlock…), if it is one.</summary>
    public static char? TileChar(BinElement e)
    {
        if (e.Int("width") <= 0 || e.Int("height") <= 0) return null;
        foreach (string k in new[] { "tiletype", "tileType", "tiles" })
            if (e.Find(k)?.Value is string s && s.Length == 1) return s[0];
        return null;
    }

    public static string? SpikeDir(string name)
    {
        foreach (string d in new[] { "Up", "Down", "Left", "Right" })
            if (name.EndsWith("spikes" + d, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    /// <summary>Atlas path and justification of an entity's sprite, if it has one.</summary>
    public static (string Path, PointF Justify)? SpriteFor(BinElement e)
    {
        if (MapModel.IsSpawn(e)) return ("characters/player/sitDown00", new PointF(0.5f, 1));
        if (e.Name == "crystalStaticSpinner")
        {
            string color = e.Str("color", "Blue").ToLowerInvariant();
            if (color is "rainbow" or "core") color = "white";
            return ($"danger/crystal/fg_{color}00", new PointF(0.5f, 0.5f));
        }
        if (PluginCatalog.Entity(e.Name) is { Texture: { } tex } def)
            return (tex, def.Justify ?? new PointF(0.5f, 0.5f));
        return null;
    }

    /// <summary>World-space bounds of a loaded sprite for the entity, or null.</summary>
    public static RectangleF? SpriteBounds(Room room, BinElement e)
    {
        if (SpriteFor(e) is not { } s || Sprite(s.Path) is not { } tex) return null;
        float x = room.X + e.Num("x"), y = room.Y + e.Num("y");
        return new RectangleF(x - tex.W * s.Justify.X, y - tex.H * s.Justify.Y, tex.W, tex.H);
    }
}
