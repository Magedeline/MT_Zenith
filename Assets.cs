using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace CELMapViewer;

/// <summary>A decoded texture as premultiplied ARGB pixels (the layout of <see cref="PixelFormat.Format32bppPArgb"/>).</summary>
public sealed class Tex
{
    public readonly int W, H;
    public readonly int[] Pixels;
    private Bitmap? _bmp;

    public Tex(int w, int h, int[] px) { W = w; H = h; Pixels = px; }

    public Bitmap Bitmap => _bmp ??= ToBitmap(Pixels, W, H);

    public static Bitmap ToBitmap(int[] px, int w, int h)
    {
        var b = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppPArgb);
        if (w <= 0 || h <= 0) return b;
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(px, y * w, d.Scan0 + y * d.Stride, w);
        b.UnlockBits(d);
        return b;
    }

    public static Tex FromBitmap(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        var px = new int[w * h];
        var d = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * w, w);
        src.UnlockBits(d);
        return new Tex(w, h, px);
    }

    /// <summary>Premultiplied "over" blend of one pixel onto another.</summary>
    public static int Over(int dst, int src)
    {
        uint sa = (uint)src >> 24;
        if (sa == 255 || dst == 0) return src;
        if (sa == 0) return dst;
        uint inv = 255 - sa, d = (uint)dst, s = (uint)src;
        uint a = sa + ((d >> 24) * inv + 127) / 255;
        uint r = ((s >> 16) & 255) + (((d >> 16) & 255) * inv + 127) / 255;
        uint g = ((s >> 8) & 255) + (((d >> 8) & 255) * inv + 127) / 255;
        uint b = (s & 255) + ((d & 255) * inv + 127) / 255;
        return (int)((Math.Min(a, 255) << 24) | (Math.Min(r, 255) << 16) | (Math.Min(g, 255) << 8) | Math.Min(b, 255));
    }

    /// <summary>Draws a region of this texture onto a pixel buffer (nearest sampling, optional flips/scale).</summary>
    public void BlitTo(int[] dst, int dstW, int dstH, int sx, int sy, int sw, int sh, int dx, int dy, float scaleX = 1, float scaleY = 1)
    {
        int ow = (int)MathF.Round(sw * MathF.Abs(scaleX)), oh = (int)MathF.Round(sh * MathF.Abs(scaleY));
        if (ow <= 0 || oh <= 0) return;
        bool unit = MathF.Abs(scaleX) == 1 && MathF.Abs(scaleY) == 1;
        for (int y = 0; y < oh; y++)
        {
            int ty = dy + y;
            if ((uint)ty >= (uint)dstH) continue;
            int srcY = unit ? y : (int)(y / MathF.Abs(scaleY));
            if (scaleY < 0) srcY = sh - 1 - srcY;
            srcY += sy;
            if ((uint)srcY >= (uint)H) continue;
            int row = srcY * W, drow = ty * dstW;
            for (int x = 0; x < ow; x++)
            {
                int tx = dx + x;
                if ((uint)tx >= (uint)dstW) continue;
                int srcX = unit ? x : (int)(x / MathF.Abs(scaleX));
                if (scaleX < 0) srcX = sw - 1 - srcX;
                srcX += sx;
                if ((uint)srcX >= (uint)W) continue;
                int p = Pixels[row + srcX];
                if (p == 0) continue;
                dst[drow + tx] = Over(dst[drow + tx], p);
            }
        }
    }
}

/// <summary>
/// Read-only access to game content: vanilla Content/, loose mod folders and mod zips under Mods/.
/// Paths are mod-relative with forward slashes ("Graphics/ForegroundTiles.xml").
/// </summary>
public static class GameAssets
{
    public static readonly string? CelesteDir = GameLink.FindCelesteDir();
    public static readonly string? ModDir = GameLink.FindModDir();

    private static readonly object Gate = new();
    private static List<string>? _looseMods;
    private static Dictionary<string, (string Zip, string Entry)>? _zipIndex;
    private static readonly Dictionary<string, ZipArchive> OpenZips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loose mod folders, this mod first.</summary>
    public static IReadOnlyList<string> LooseMods
    {
        get
        {
            lock (Gate)
            {
                if (_looseMods != null) return _looseMods;
                var list = new List<string>();
                if (ModDir != null) list.Add(ModDir);
                string? mods = ModsDir;
                if (mods != null)
                    foreach (string d in Directory.EnumerateDirectories(mods))
                        if (!string.Equals(Path.GetFullPath(d).TrimEnd('\\'), ModDir?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(d).Equals("Cache", StringComparison.OrdinalIgnoreCase))
                            list.Add(d);
                return _looseMods = list;
            }
        }
    }

    public static string? ModsDir =>
        CelesteDir != null && Directory.Exists(Path.Combine(CelesteDir, "Mods")) ? Path.Combine(CelesteDir, "Mods")
        : ModDir != null ? Path.GetDirectoryName(ModDir) : null;

    private static Dictionary<string, (string Zip, string Entry)> ZipIndex
    {
        get
        {
            lock (Gate)
            {
                if (_zipIndex != null) return _zipIndex;
                var idx = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                if (ModsDir != null)
                    foreach (string zip in Directory.EnumerateFiles(ModsDir, "*.zip").OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var z = ZipFile.OpenRead(zip);
                            foreach (var e in z.Entries)
                                if (e.Length > 0) idx.TryAdd(e.FullName.Replace('\\', '/'), (zip, e.FullName));
                        }
                        catch { /* unreadable zip: skip */ }
                    }
                return _zipIndex = idx;
            }
        }
    }

    /// <summary>A file from a mod (this one first, then other loose mods, then zips).</summary>
    public static byte[]? ReadModFile(string rel)
    {
        rel = rel.Replace('\\', '/').TrimStart('/');
        foreach (string mod in LooseMods)
        {
            string p = Path.Combine(mod, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) return File.ReadAllBytes(p);
        }
        return ZipIndex.TryGetValue(rel, out var z) ? ReadZip(z.Zip, z.Entry) : null;
    }

    private static byte[]? ReadZip(string zip, string entry)
    {
        lock (Gate)
        {
            try
            {
                if (!OpenZips.TryGetValue(zip, out var arc)) OpenZips[zip] = arc = ZipFile.OpenRead(zip);
                var e = arc.GetEntry(entry);
                if (e == null) return null;
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }
    }

    /// <summary>Files under a mod-relative folder with an extension, as (source, relative path, reader).</summary>
    public static IEnumerable<(string Source, string Rel, Func<byte[]?> Read)> EnumerateModFiles(string folder, string ext) =>
        EnumerateModFilesStamped(folder, ext).Select(f => (f.Source, f.Rel, f.Read));

    /// <summary>
    /// Like <see cref="EnumerateModFiles"/>, plus a change stamp (size and write time of the loose file or of its zip)
    /// that lets callers cache derived data without reading the file.
    /// </summary>
    public static IEnumerable<(string Source, string Rel, string Stamp, Func<byte[]?> Read)> EnumerateModFilesStamped(string folder, string ext)
    {
        static string StampOf(string path)
        {
            try { var fi = new FileInfo(path); return fi.Length + ":" + fi.LastWriteTimeUtc.Ticks; }
            catch { return ""; }
        }
        folder = folder.Replace('\\', '/').Trim('/') + "/";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string mod in LooseMods)
        {
            string dir = Path.Combine(mod, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;
            string src = Path.GetFileName(mod.TrimEnd('\\', '/'));
            foreach (string f in Directory.EnumerateFiles(dir, "*" + ext, SearchOption.AllDirectories))
            {
                string rel = folder + Path.GetRelativePath(dir, f).Replace('\\', '/');
                if (seen.Add(src + "|" + rel)) yield return (src, rel, StampOf(f), () => File.ReadAllBytes(f));
            }
        }
        var zipStamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, z) in ZipIndex)
            if (rel.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && rel.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                var zz = z;
                if (!zipStamps.TryGetValue(z.Zip, out string? stamp)) zipStamps[z.Zip] = stamp = StampOf(z.Zip);
                yield return (Path.GetFileNameWithoutExtension(z.Zip), rel, stamp, () => ReadZip(zz.Zip, zz.Entry));
            }
    }

    /// <summary>A vanilla content file (Celeste/Content/...).</summary>
    public static string? VanillaFile(string rel)
    {
        if (CelesteDir == null) return null;
        string p = Path.Combine(CelesteDir, "Content", rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(p) ? p : null;
    }

    public static readonly Atlas Gameplay = new("Gameplay");
}

/// <summary>A Celeste texture atlas: vanilla packed pages (.meta + .data) with loose PNG overrides from mods.</summary>
public sealed class Atlas
{
    private readonly string _name;
    private readonly object _gate = new();
    private Dictionary<string, (int Page, short X, short Y, short W, short H, short FX, short FY, short FW, short FH)>? _index;
    private List<string>? _pages;
    private readonly Dictionary<int, Tex?> _pageTex = new();
    private readonly Dictionary<string, Tex?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Atlas(string name) { _name = name; }

    /// <summary>A texture by atlas path ("objects/refill/idle00"), or null.</summary>
    public Tex? Get(string path)
    {
        path = path.Replace('\\', '/').Trim('/');
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var t)) return t;
            t = LoadMod(path) ?? LoadVanilla(path);
            _cache[path] = t;
            return t;
        }
    }

    /// <summary>First frame of a texture or animation ("path", "path00", "path0", "path000").</summary>
    public Tex? GetFirstFrame(string path) =>
        Get(path) ?? Get(path + "00") ?? Get(path + "0") ?? Get(path + "000");

    /// <summary>Vanilla packed texture paths starting with a prefix ("decals/").</summary>
    public List<string> Keys(string prefix)
    {
        lock (_gate)
        {
            EnsureIndex();
            return _index!.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private Tex? LoadMod(string path)
    {
        byte[]? png = GameAssets.ReadModFile($"Graphics/Atlases/{_name}/{path}.png");
        if (png == null) return null;
        try
        {
            using var ms = new MemoryStream(png);
            using var bmp = new Bitmap(ms);
            return Tex.FromBitmap(bmp);
        }
        catch { return null; }
    }

    private void EnsureIndex()
    {
        if (_index != null) return;
        _index = new(StringComparer.OrdinalIgnoreCase);
        _pages = new();
        string? meta = GameAssets.VanillaFile($"Graphics/Atlases/{_name}.meta");
        if (meta == null) return;
        try
        {
            using var r = new BinaryReader(File.OpenRead(meta));
            r.ReadInt32(); r.ReadString(); r.ReadInt32();
            int pages = r.ReadInt16();
            for (int p = 0; p < pages; p++)
            {
                _pages.Add(r.ReadString());
                int n = r.ReadInt16();
                for (int i = 0; i < n; i++)
                {
                    string key = r.ReadString().Replace('\\', '/');
                    _index[key] = (p, r.ReadInt16(), r.ReadInt16(), r.ReadInt16(), r.ReadInt16(),
                                   r.ReadInt16(), r.ReadInt16(), r.ReadInt16(), r.ReadInt16());
                }
            }
        }
        catch { /* partial index is still useful */ }
    }

    private Tex? LoadVanilla(string path)
    {
        EnsureIndex();
        if (!_index!.TryGetValue(path, out var s)) return null;
        if (!_pageTex.TryGetValue(s.Page, out var page))
        {
            string? data = GameAssets.VanillaFile($"Graphics/Atlases/{_pages![s.Page]}.data");
            _pageTex[s.Page] = page = data == null ? null : DecodeData(File.ReadAllBytes(data));
        }
        if (page == null) return null;
        int fw = Math.Max(s.FW, s.W), fh = Math.Max(s.FH, s.H);
        var px = new int[fw * fh];
        for (int y = 0; y < s.H; y++)
        {
            int ty = y - s.FY;
            if ((uint)ty >= (uint)fh) continue;
            for (int x = 0; x < s.W; x++)
            {
                int tx = x - s.FX;
                if ((uint)tx >= (uint)fw) continue;
                px[ty * fw + tx] = page.Pixels[(s.Y + y) * page.W + s.X + x];
            }
        }
        return new Tex(fw, fh, px);
    }

    /// <summary>Celeste's run-length ".data" texture format (premultiplied BGRA runs).</summary>
    public static Tex DecodeData(byte[] bytes)
    {
        int w = BitConverter.ToInt32(bytes, 0), h = BitConverter.ToInt32(bytes, 4);
        bool alpha = bytes[8] == 1;
        int p = 9, i = 0, total = w * h;
        var px = new int[total];
        while (i < total && p < bytes.Length)
        {
            int run = bytes[p], c;
            if (alpha)
            {
                byte a = bytes[p + 1];
                if (a > 0) { c = (a << 24) | (bytes[p + 4] << 16) | (bytes[p + 3] << 8) | bytes[p + 2]; p += 5; }
                else { c = 0; p += 2; }
            }
            else { c = (255 << 24) | (bytes[p + 3] << 16) | (bytes[p + 2] << 8) | bytes[p + 1]; p += 4; }
            int n = Math.Min(run, total - i);
            if (c != 0) px.AsSpan(i, n).Fill(c);
            i += n;
        }
        return new Tex(w, h, px);
    }
}
