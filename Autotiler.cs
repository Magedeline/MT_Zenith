using System.Xml.Linq;

namespace CELMapViewer;

/// <summary>
/// Celeste's autotiler: picks each tile's 8x8 sprite from its neighbourhood using the masks in
/// ForegroundTiles.xml / BackgroundTiles.xml (copy, ignores, padding and center sets included).
/// </summary>
public sealed class Autotiler
{
    internal sealed class MaskSet
    {
        public required byte[] Mask; // 0 empty, 1 solid, 2 any
        public required int W, H;
        public required Point[] Tiles;
        public int Wildcards => Mask.Count(m => m == 2);
    }

    public sealed class Tileset
    {
        public char Id;
        public string Path = "";
        public string DisplayName = "";
        internal string Ignores = "", IgnoreExceptions = "";
        internal List<MaskSet> Masked = new();
        internal Point[] Center = Array.Empty<Point>(), Padding = Array.Empty<Point>();
        internal Tex? Texture;
        internal bool TextureLoaded;

        internal bool Ignore(char c)
        {
            if (c == Id) return false;
            if (IgnoreExceptions.Contains(c)) return false;
            return Ignores.Contains(c) || Ignores.Contains('*');
        }

        public override string ToString() => $"{Id}  {DisplayName}";
    }

    public readonly string Source;
    private readonly Dictionary<char, Tileset> _sets = new();
    public IEnumerable<Tileset> Tilesets => _sets.Values;

    private Autotiler(string source) { Source = source; }

    public bool Has(char c) => _sets.ContainsKey(c);
    public Tileset? Get(char c) => _sets.GetValueOrDefault(c);

    // ---- loading ----

    private static readonly Dictionary<string, Autotiler?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The autotiler for a map's tileset xml (mod-relative path from the map meta, e.g. "Graphics/ForegroundTiles.xml"),
    /// resolved like Everest: the map's own mod first, then any mod, then vanilla. Null path = vanilla.
    /// </summary>
    public static Autotiler? For(string? modRelPath, bool fg, string? mapModDir)
    {
        string vanillaRel = fg ? "Graphics/ForegroundTiles.xml" : "Graphics/BackgroundTiles.xml";
        string key = (modRelPath ?? "<vanilla>" + vanillaRel) + "|" + mapModDir;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var a)) return a;
            string? text = null, source = null;
            if (!string.IsNullOrWhiteSpace(modRelPath))
            {
                string rel = modRelPath.Replace('\\', '/').TrimStart('/');
                if (!rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) rel += ".xml";
                string? local = mapModDir != null ? System.IO.Path.Combine(mapModDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)) : null;
                if (local != null && File.Exists(local)) { text = File.ReadAllText(local); source = local; }
                else if (GameAssets.ReadModFile(rel) is { } bytes) { text = System.Text.Encoding.UTF8.GetString(bytes); source = rel; }
            }
            if (text == null && GameAssets.VanillaFile(vanillaRel) is { } v) { text = File.ReadAllText(v); source = v; }
            try { a = text == null ? null : Parse(text, source!); }
            catch { a = null; }
            Cache[key] = a;
            return a;
        }
    }

    public static void ClearCache() { lock (Cache) Cache.Clear(); }

    public static Autotiler Parse(string xml, string source)
    {
        var at = new Autotiler(source);
        var doc = XDocument.Parse(xml.TrimStart('\uFEFF'));
        var byId = new Dictionary<char, XElement>();
        foreach (var x in doc.Root!.Elements("Tileset"))
        {
            string id = (string?)x.Attribute("id") ?? "";
            if (id.Length == 0) continue;
            var ts = new Tileset { Id = id[0], Path = (string?)x.Attribute("path") ?? "" };
            ts.DisplayName = (string?)x.Attribute("displayName") ?? Pretty(ts.Path);
            if ((string?)x.Attribute("copy") is { Length: > 0 } copy && byId.TryGetValue(copy[0], out var src))
                ReadSets(ts, src);
            ReadSets(ts, x);
            ts.Ignores = ((string?)x.Attribute("ignores") ?? "").Replace(",", "");
            ts.IgnoreExceptions = ((string?)x.Attribute("ignoreExceptions") ?? "").Replace(",", "");
            ts.Masked = ts.Masked.OrderBy(m => m.Wildcards).ToList();
            byId[ts.Id] = x;
            at._sets[ts.Id] = ts;
        }
        return at;
    }

    private static string Pretty(string path)
    {
        string n = path.Replace('\\', '/').Split('/').Last();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n.Length; i++)
        {
            char c = n[i];
            if (c == '_') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c) && char.IsLower(n[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    private static void ReadSets(Tileset ts, XElement x)
    {
        foreach (var set in x.Elements("set"))
        {
            string mask = (string?)set.Attribute("mask") ?? "";
            var tiles = ParseTiles((string?)set.Attribute("tiles") ?? "");
            if (mask == "center") ts.Center = tiles;
            else if (mask == "padding") ts.Padding = tiles;
            else
            {
                string[] rows = mask.Split('-');
                int h = rows.Length, w = rows.Max(r => r.Length);
                if (w % 2 == 0 || h % 2 == 0) continue;
                var m = new byte[w * h];
                for (int y = 0; y < h; y++)
                    for (int xx = 0; xx < w; xx++)
                    {
                        char c = xx < rows[y].Length ? rows[y][xx] : 'x';
                        m[y * w + xx] = c == '0' ? (byte)0 : c == '1' ? (byte)1 : (byte)2;
                    }
                ts.Masked.RemoveAll(s => s.W == w && s.H == h && s.Mask.AsSpan().SequenceEqual(m));
                ts.Masked.Add(new MaskSet { Mask = m, W = w, H = h, Tiles = tiles });
            }
        }
    }

    private static Point[] ParseTiles(string s) =>
        s.Split(';', StringSplitOptions.RemoveEmptyEntries)
         .Select(p => p.Split(','))
         .Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
         .Select(p => new Point(int.Parse(p[0]), int.Parse(p[1])))
         .ToArray();

    private Tex? TextureOf(Tileset ts)
    {
        if (!ts.TextureLoaded)
        {
            ts.TextureLoaded = true;
            ts.Texture = GameAssets.Gameplay.Get("tilesets/" + ts.Path);
        }
        return ts.Texture;
    }

    // ---- generation ----

    /// <summary>
    /// Autotiles <paramref name="grid"/> into a premultiplied pixel buffer (8 px per tile), blending over what is there.
    /// <paramref name="edgesExtend"/>: outside the grid repeats the edge (rooms) instead of being empty (tile entities).
    /// </summary>
    public void Render(TileGrid grid, int[] dst, int dstW, int dstH, int ox, int oy, bool edgesExtend)
    {
        for (int y = 0; y < grid.Rows; y++)
            for (int x = 0; x < grid.Cols; x++)
            {
                char c = grid[x, y];
                if (c == '0' || c == ' ') continue;
                int px = ox + x * 8, py = oy + y * 8;
                if (px >= dstW || py >= dstH || px + 8 <= 0 || py + 8 <= 0) continue;
                if (!_sets.TryGetValue(c, out var ts) || TextureOf(ts) is not { } tex)
                {
                    FillFlat(dst, dstW, dstH, px, py, FlatColor(c, Source.Contains("Background", StringComparison.OrdinalIgnoreCase)));
                    continue;
                }
                var tile = Choose(ts, grid, x, y, edgesExtend);
                if (tile is not { } t) continue;
                tex.BlitTo(dst, dstW, dstH, t.X * 8, t.Y * 8, 8, 8, px, py);
            }
    }

    private static void FillFlat(int[] dst, int w, int h, int px, int py, int color)
    {
        for (int y = Math.Max(0, py); y < Math.Min(h, py + 8); y++)
            for (int x = Math.Max(0, px); x < Math.Min(w, px + 8); x++)
                dst[y * w + x] = color;
    }

    public static int FlatColor(char c, bool bg)
    {
        int hsh = (c * 2654435761u).GetHashCode();
        int r = 90 + (hsh & 0x3F), g = 90 + ((hsh >> 6) & 0x3F), b = 100 + ((hsh >> 12) & 0x3F);
        return bg ? Color.FromArgb(255, r / 2, g / 2, b / 2 + 10).ToArgb() : Color.FromArgb(255, r + 40, g + 40, b + 40).ToArgb();
    }

    private static bool Solid(Tileset ts, TileGrid g, int x, int y, bool edgesExtend)
    {
        if (x < 0 || y < 0 || x >= g.Cols || y >= g.Rows)
        {
            if (!edgesExtend) return false;
            x = Math.Clamp(x, 0, g.Cols - 1);
            y = Math.Clamp(y, 0, g.Rows - 1);
        }
        char c = g[x, y];
        return c != '0' && c != ' ' && !ts.Ignore(c);
    }

    private static Point? Choose(Tileset ts, TileGrid g, int x, int y, bool edgesExtend)
    {
        bool full = true;
        for (int dy = -1; dy <= 1 && full; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (!Solid(ts, g, x + dx, y + dy, edgesExtend)) { full = false; break; }

        Point[]? tiles = null;
        if (full && ts.Masked.All(m => m.W == 3 && m.H == 3))
        {
            bool padded = !Solid(ts, g, x - 2, y, edgesExtend) || !Solid(ts, g, x + 2, y, edgesExtend)
                       || !Solid(ts, g, x, y - 2, edgesExtend) || !Solid(ts, g, x, y + 2, edgesExtend);
            tiles = padded ? ts.Padding : ts.Center;
        }
        else
        {
            foreach (var m in ts.Masked)
            {
                bool ok = true;
                int hw = m.W / 2, hh = m.H / 2;
                for (int my = 0; my < m.H && ok; my++)
                    for (int mx = 0; mx < m.W; mx++)
                    {
                        byte want = m.Mask[my * m.W + mx];
                        if (want == 2) continue;
                        if ((want == 1) != Solid(ts, g, x + mx - hw, y + my - hh, edgesExtend)) { ok = false; break; }
                    }
                if (ok) { tiles = m.Tiles; break; }
            }
            if (tiles == null && full)
            {
                bool padded = !Solid(ts, g, x - 2, y, edgesExtend) || !Solid(ts, g, x + 2, y, edgesExtend)
                           || !Solid(ts, g, x, y - 2, edgesExtend) || !Solid(ts, g, x, y + 2, edgesExtend);
                tiles = padded ? ts.Padding : ts.Center;
            }
        }
        if (tiles == null || tiles.Length == 0) return null;
        uint h = (uint)(x * 73856093 ^ y * 19349663) * 2654435761u;
        return tiles[(int)((h >> 8) % (uint)tiles.Length)];
    }
}

/// <summary>A room's tile layer: one char per 8x8 cell ('0' = empty), as stored in the map's text grids.</summary>
public sealed class TileGrid
{
    public readonly int Cols, Rows;
    private readonly char[] _cells;

    public TileGrid(int cols, int rows, char fill = '0')
    {
        Cols = Math.Max(0, cols); Rows = Math.Max(0, rows);
        _cells = new char[Cols * Rows];
        Array.Fill(_cells, fill);
    }

    public char this[int x, int y]
    {
        get => (uint)x < (uint)Cols && (uint)y < (uint)Rows ? _cells[y * Cols + x] : '0';
        set { if ((uint)x < (uint)Cols && (uint)y < (uint)Rows) _cells[y * Cols + x] = value; }
    }

    public static TileGrid Parse(string text, int cols, int rows)
    {
        var g = new TileGrid(cols, rows);
        string[] lines = text.Replace("\r", "").Split('\n');
        for (int y = 0; y < rows && y < lines.Length; y++)
            for (int x = 0; x < cols && x < lines[y].Length; x++)
                g._cells[y * cols + x] = lines[y][x] == ' ' ? '0' : lines[y][x];
        return g;
    }

    /// <summary>Rows joined by '\n' with trailing empties trimmed, like Lönn writes them.</summary>
    public string Serialize()
    {
        var rows = new List<string>(Rows);
        for (int y = 0; y < Rows; y++) rows.Add(new string(_cells, y * Cols, Cols).TrimEnd('0'));
        while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
        return string.Join("\n", rows);
    }

    public TileGrid Clone() => Resized(Cols, Rows, 0, 0);

    /// <summary>A copy resized to
    public TileGrid Resized(int cols, int rows, int dx, int dy)
    {
        var g = new TileGrid(cols, rows);
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
                g[x + dx, y + dy] = this[x, y];
        return g;
    }

    /// <summary>Flood-fills the 4-connected region of the cell's char. Returns whether anything changed.</summary>
    public bool Fill(int x, int y, char c)
    {
        char from = this[x, y];
        if (from == c || (uint)x >= (uint)Cols || (uint)y >= (uint)Rows) return false;
        var stack = new Stack<(int, int)>();
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            if ((uint)cx >= (uint)Cols || (uint)cy >= (uint)Rows || _cells[cy * Cols + cx] != from) continue;
            _cells[cy * Cols + cx] = c;
            stack.Push((cx + 1, cy)); stack.Push((cx - 1, cy)); stack.Push((cx, cy + 1)); stack.Push((cx, cy - 1));
        }
        return true;
    }
}
