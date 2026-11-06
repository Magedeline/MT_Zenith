using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CELMapViewer.Agent;

/// <summary>Decoded audio: one float array per channel (−1..1).</summary>
public sealed class Pcm
{
    public int Rate;
    public float[][] Ch = Array.Empty<float[]>();
    /// <summary>Seconds into the source where this clip starts.</summary>
    public double Start;
    public double SourceDuration;
    public int SourceRate, SourceChannels;
    public string Codec = "";
    public int Length => Ch.Length == 0 ? 0 : Ch[0].Length;
    public double Duration => Rate == 0 ? 0 : (double)Length / Rate;
}

/// <summary>
/// Image, audio and video loading and analysis for the AI tools and the agent chat's attachments. Images use
/// System.Drawing; WAV is parsed directly; everything else (ogg/mp3/flac, video, webp) goes through ffmpeg when installed.
/// </summary>
public static partial class Media
{
    public static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp" };
    public static readonly string[] AudioExts = { ".wav", ".ogg", ".mp3", ".flac", ".m4a", ".aac", ".opus", ".wma", ".aif", ".aiff" };
    public static readonly string[] VideoExts = { ".mp4", ".webm", ".ogv", ".avi", ".mov", ".mkv", ".wmv", ".m4v", ".mpg", ".mpeg" };

    /// <summary>image, audio, video, bank (FMOD) or null.</summary>
    public static string? KindOf(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ImageExts.Contains(ext)) return "image";
        if (AudioExts.Contains(ext)) return "audio";
        if (VideoExts.Contains(ext)) return "video";
        return ext == ".bank" ? "bank" : null;
    }

    public static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp",
        ".wav" => "audio/wav", ".ogg" => "audio/ogg", ".mp3" => "audio/mpeg", ".flac" => "audio/flac",
        ".mp4" or ".m4v" => "video/mp4", ".webm" => "video/webm", ".ogv" => "video/ogg", ".mov" => "video/quicktime",
        _ => "application/octet-stream"
    };

    private static double R(double v, int digits = 2) => double.IsFinite(v) ? Math.Round(v, digits) : 0;
    public static double Db(double v) => v <= 1e-10 ? -200 : 20 * Math.Log10(v);

    // ---- ffmpeg ----

    public const string NoFfmpeg = "ffmpeg/ffprobe were not found. Install FFmpeg (e.g. `winget install Gyan.FFmpeg`) or set the CEL_FFMPEG " +
                                   "environment variable to ffmpeg.exe or its folder, then restart the viewer.";
    private static readonly object FindGate = new();
    private static bool _searched;
    private static string? _ffmpeg, _ffprobe;

    public static string? Ffmpeg { get { FindFfmpeg(); return _ffmpeg; } }
    public static string? Ffprobe { get { FindFfmpeg(); return _ffprobe; } }

    private static void FindFfmpeg()
    {
        lock (FindGate)
        {
            if (_searched) return;
            _searched = true;
            var dirs = new List<string>();
            if (Environment.GetEnvironmentVariable("CEL_FFMPEG") is { Length: > 0 } env)
                dirs.Add(File.Exists(env) ? Path.GetDirectoryName(Path.GetFullPath(env))! : env);
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
            try
            {
                string pk = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(pk))
                    foreach (string d in Directory.EnumerateDirectories(pk, "*FFmpeg*"))
                        dirs.AddRange(Directory.EnumerateDirectories(d, "bin", SearchOption.AllDirectories));
            }
            catch { }
            dirs.Add(@"C:\ffmpeg\bin");
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"));
            foreach (string d in dirs)
            {
                try
                {
                    if (_ffmpeg == null && File.Exists(Path.Combine(d, "ffmpeg.exe"))) _ffmpeg = Path.Combine(d, "ffmpeg.exe");
                    if (_ffprobe == null && File.Exists(Path.Combine(d, "ffprobe.exe"))) _ffprobe = Path.Combine(d, "ffprobe.exe");
                }
                catch { }
                if (_ffmpeg != null && _ffprobe != null) break;
            }
        }
    }

    /// <summary>Runs a tool with stdin closed (ffmpeg must never read the MCP stdio stream), capturing stdout bytes and stderr.</summary>
    internal static (int Code, byte[] Out, string Err) Run(string exe, IEnumerable<string> args, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new ToolException("Could not start " + exe);
        p.StandardInput.Close();
        var ms = new MemoryStream();
        var outTask = p.StandardOutput.BaseStream.CopyToAsync(ms);
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            throw new ToolException($"{Path.GetFileName(exe)} timed out after {timeoutMs / 1000}s.");
        }
        outTask.Wait();
        return (p.ExitCode, ms.ToArray(), errTask.Result);
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>ffprobe's JSON (format + streams).</summary>
    public static JsonObject Probe(string path)
    {
        string exe = Ffprobe ?? throw new ToolException(NoFfmpeg);
        var (code, outp, err) = Run(exe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, 30_000);
        if (code != 0) throw new ToolException("ffprobe could not read the file: " + err.Trim());
        return JsonNode.Parse(outp) as JsonObject ?? throw new ToolException("ffprobe returned no data.");
    }

    private static double Num(JsonNode? n)
    {
        string? s = n?.ToString();
        if (string.IsNullOrEmpty(s)) return 0;
        int slash = s.IndexOf('/');
        if (slash > 0 && double.TryParse(s[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                      && double.TryParse(s[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            return b == 0 ? 0 : a / b;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
    }

    public static JsonObject? FirstStream(JsonObject probe, string type) =>
        (probe["streams"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(s => s["codec_type"]?.ToString() == type
                                                                                  && s["disposition"]?["attached_pic"]?.ToString() != "1");

    public static double DurationOf(JsonObject probe) =>
        Num(probe["format"]?["duration"]) is > 0 and var d ? d : Num(FirstStream(probe, "video")?["duration"] ?? FirstStream(probe, "audio")?["duration"]);

    /// <summary>A compact summary of an ffprobe result.</summary>
    public static JsonObject Summary(JsonObject probe)
    {
        var o = new JsonObject
        {
            ["container"] = probe["format"]?["format_long_name"]?.ToString() ?? probe["format"]?["format_name"]?.ToString(),
            ["duration_s"] = R(DurationOf(probe), 3),
            ["size_bytes"] = (long)Num(probe["format"]?["size"]),
            ["bit_rate_kbps"] = R(Num(probe["format"]?["bit_rate"]) / 1000, 0)
        };
        if (FirstStream(probe, "video") is { } v)
            o["video"] = new JsonObject
            {
                ["codec"] = v["codec_name"]?.ToString(), ["width"] = (int)Num(v["width"]), ["height"] = (int)Num(v["height"]),
                ["fps"] = R(Num(v["avg_frame_rate"]) is > 0 and var fr ? fr : Num(v["r_frame_rate"]), 3),
                ["frames"] = (long)Num(v["nb_frames"]), ["pixel_format"] = v["pix_fmt"]?.ToString()
            };
        if (FirstStream(probe, "audio") is { } a)
            o["audio"] = new JsonObject
            {
                ["codec"] = a["codec_name"]?.ToString(), ["sample_rate"] = (int)Num(a["sample_rate"]), ["channels"] = (int)Num(a["channels"])
            };
        if (probe["format"]?["tags"] is JsonObject tags && tags.Count > 0) o["tags"] = tags.DeepClone();
        return o;
    }

    /// <summary>Splits a concatenated image2pipe PNG stream into individual PNG files.</summary>
    internal static List<byte[]> SplitPngs(byte[] data)
    {
        var list = new List<byte[]>();
        int pos = 0;
        while (pos + 8 <= data.Length && data[pos] == 0x89 && data[pos + 1] == (byte)'P')
        {
            int p = pos + 8;
            bool end = false;
            while (p + 12 <= data.Length && !end)
            {
                int len = (data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
                end = data[p + 4] == 'I' && data[p + 5] == 'E' && data[p + 6] == 'N' && data[p + 7] == 'D';
                p += 12 + len;
            }
            if (!end || p > data.Length) break;
            list.Add(data[pos..p]);
            pos = p;
        }
        return list;
    }

    internal static Bitmap FromPng(byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }

    // ---- images ----

    /// <summary>Loads up to <paramref name="maxFrames"/> frames (GIFs are animated) as 32bpp ARGB bitmaps; delays in ms per frame.</summary>
    public static List<Bitmap> LoadImageFrames(string path, int maxFrames, out List<int> delaysMs, out int totalFrames)
    {
        delaysMs = new List<int>();
        var frames = new List<Bitmap>();
        try
        {
            using var fs = File.OpenRead(path);
            using var img = Image.FromStream(fs);
            totalFrames = 1;
            if (img.FrameDimensionsList.Contains(FrameDimension.Time.Guid))
            {
                totalFrames = img.GetFrameCount(FrameDimension.Time);
                int[] delays = Array.Empty<int>();
                try
                {
                    var item = img.GetPropertyItem(0x5100);
                    if (item?.Value is { } v) delays = Enumerable.Range(0, v.Length / 4).Select(i => BitConverter.ToInt32(v, i * 4) * 10).ToArray();
                }
                catch { }
                for (int i = 0; i < Math.Min(totalFrames, maxFrames); i++)
                {
                    img.SelectActiveFrame(FrameDimension.Time, i);
                    frames.Add(new Bitmap(img));
                    delaysMs.Add(i < delays.Length ? delays[i] : 0);
                }
                return frames;
            }
            frames.Add(new Bitmap(img));
            return frames;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException)
        {
            // Not a GDI+ format (webp, ...): let ffmpeg decode the first frame.
            string exe = Ffmpeg ?? throw new ToolException($"Can't decode {Path.GetFileName(path)} without ffmpeg. " + NoFfmpeg);
            var (code, outp, err) = Run(exe, new[] { "-v", "error", "-nostdin", "-i", path, "-frames:v", "1", "-f", "image2pipe", "-c:v", "png", "-" }, 30_000);
            if (code != 0 || outp.Length == 0) throw new ToolException("Could not decode image: " + err.Trim());
            totalFrames = 1;
            return new List<Bitmap> { FromPng(outp) };
        }
    }

    private static int[] Pixels(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * bmp.Width, bmp.Width);
            return px;
        }
        finally { bmp.UnlockBits(data); }
    }

    private static string Hex(int p)
    {
        int a = (p >> 24) & 255, r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
        return a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{r:X2}{g:X2}{b:X2}{a:X2}";
    }

    /// <summary>Content statistics: opaque bounds, transparency, colour count, palette, brightness.</summary>
    public static JsonObject ImageStats(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var px = Pixels(bmp);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, transparent = 0, partial = 0;
        double lum = 0, sat = 0;
        var counts = new Dictionary<int, int>();
        bool overflow = false;
        for (int y = 0, i = 0; y < h; y++)
            for (int x = 0; x < w; x++, i++)
            {
                int p = px[i];
                int a = (p >> 24) & 255;
                if (a == 0) { transparent++; continue; }
                if (a < 255) partial++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
                lum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                sat += mx == 0 ? 0 : (mx - mn) / (double)mx;
                if (!overflow)
                {
                    counts[p] = counts.GetValueOrDefault(p) + 1;
                    if (counts.Count > 65536) overflow = true;
                }
            }
        int opaque = w * h - transparent;
        var o = new JsonObject
        {
            ["width"] = w, ["height"] = h,
            ["transparent_pct"] = R(100.0 * transparent / Math.Max(1, w * h), 1),
            ["semi_transparent_pixels"] = partial,
            ["colors"] = overflow ? ">65536 (photo/gradient-like)" : counts.Count
        };
        if (opaque > 0)
        {
            o["content_bounds"] = new JsonObject { ["x"] = minX, ["y"] = minY, ["width"] = maxX - minX + 1, ["height"] = maxY - minY + 1 };
            o["mean_brightness"] = R(lum / opaque / 255, 3);
            o["mean_saturation"] = R(sat / opaque, 3);
            if (!overflow)
                o["top_colors"] = new JsonArray(counts.OrderByDescending(kv => kv.Value).Take(10)
                    .Select(kv => (JsonNode)$"{Hex(kv.Key)} {100.0 * kv.Value / opaque:0.#}%").ToArray());
        }
        else o["note"] = "fully transparent";
        return o;
    }

    private static void FillBackground(Graphics g, Rectangle r, string background)
    {
        switch (background)
        {
            case "none": return;
            case "black": g.FillRectangle(Brushes.Black, r); return;
            case "white": g.FillRectangle(Brushes.White, r); return;
            case "magenta": g.FillRectangle(Brushes.Magenta, r); return;
        }
        using var light = new SolidBrush(Color.FromArgb(204, 204, 204));
        using var dark = new SolidBrush(Color.FromArgb(150, 150, 150));
        g.FillRectangle(light, r);
        const int c = 8;
        var clip = g.Clip;
        g.SetClip(r);
        for (int y = r.Top; y < r.Bottom; y += c)
            for (int x = r.Left + ((y - r.Top) / c % 2) * c; x < r.Right; x += 2 * c)
                g.FillRectangle(dark, x, y, c, c);
        g.Clip = clip;
    }

    /// <summary>
    /// Crops and scales an image for a vision model: small pixel art is enlarged with nearest-neighbour so single pixels are visible,
    /// large images shrink to <paramref name="maxSize"/>; transparency is shown on a checkerboard unless background says otherwise.
    /// </summary>
    public static Bitmap PrepareForView(Bitmap src, Rectangle? crop, double? scale, int maxSize, string background, out double usedScale)
    {
        var full = new Rectangle(0, 0, src.Width, src.Height);
        var r = crop is { } c ? Rectangle.Intersect(c, full) : full;
        if (r.Width <= 0 || r.Height <= 0) throw new ToolException($"The crop rectangle is outside the {src.Width}x{src.Height} image.");
        int side = Math.Max(r.Width, r.Height);
        double s = scale is > 0 ? scale.Value
                 : side < 256 ? Math.Clamp(Math.Floor(512.0 / side), 1, 16)
                 : side > maxSize ? (double)maxSize / side : 1;
        if (side * s > Math.Max(maxSize, 64)) s = (double)Math.Max(maxSize, 64) / side;
        usedScale = s;
        int w = Math.Max(1, (int)Math.Round(r.Width * s)), h = Math.Max(1, (int)Math.Round(r.Height * s));
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        FillBackground(g, new Rectangle(0, 0, w, h), background);
        g.InterpolationMode = s >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = background == "none" ? CompositingMode.SourceCopy : CompositingMode.SourceOver;
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(src, new Rectangle(0, 0, w, h), r.X, r.Y, r.Width, r.Height, GraphicsUnit.Pixel, ia);
        return dst;
    }

    /// <summary>A labelled grid of images (sprite frames, folder thumbnails, video frames), all drawn at one common scale.</summary>
    public static Bitmap Sheet(IReadOnlyList<(Bitmap Img, string Label)> cells, int maxWidth, string background, int? columns = null)
    {
        if (cells.Count == 0) throw new ToolException("Nothing to show.");
        const int pad = 4, labelH = 15;
        int n = cells.Count;
        int cw = cells.Max(c => c.Img.Width), ch = cells.Max(c => c.Img.Height);
        int cols = Math.Clamp(columns ?? (int)Math.Ceiling(Math.Sqrt(n * (double)ch / Math.Max(1, cw) * 1.4)), 1, n);
        int rows = (n + cols - 1) / cols;
        int maxHeight = (int)(maxWidth * 1.4);
        double s = Math.Min((maxWidth - pad * (cols + 1)) / (double)(cols * cw), (maxHeight - rows * (labelH + pad) - pad) / (double)(rows * ch));
        if (s >= 1) s = Math.Min(Math.Floor(s), Math.Max(1, Math.Floor(160.0 / Math.Max(cw, ch))));
        s = Math.Max(s, 0.05);
        int tw = Math.Max(1, (int)Math.Ceiling(cw * s)), th = Math.Max(1, (int)Math.Ceiling(ch * s));
        int colW = Math.Max(tw, 48);
        var bmp = new Bitmap(cols * (colW + pad) + pad, rows * (th + labelH + pad) + pad, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(36, 36, 44));
        using var font = new Font("Segoe UI", 7.5f);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        for (int i = 0; i < n; i++)
        {
            var (img, label) = cells[i];
            int x0 = pad + i % cols * (colW + pad), y0 = pad + i / cols * (th + labelH + pad);
            int iw = Math.Max(1, (int)Math.Round(img.Width * s)), ih = Math.Max(1, (int)Math.Round(img.Height * s));
            var dst = new Rectangle(x0 + (colW - iw) / 2, y0 + (th - ih) / 2, iw, ih);
            FillBackground(g, dst, background);
            g.InterpolationMode = s >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(img, dst);
            g.DrawString(label, font, Brushes.Gainsboro, new RectangleF(x0, y0 + th + 1, colW, labelH), fmt);
        }
        return bmp;
    }

    public static byte[] Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Celeste-style animation frames: <c>prefix00.png</c>, <c>prefix01.png</c>, … ordered by number.</summary>
    public static List<string> SequenceFrames(string prefix)
    {
        string? dir = Path.GetDirectoryName(prefix);
        string name = Path.GetFileName(prefix);
        if (dir == null || name.Length == 0 || !Directory.Exists(dir)) return new List<string>();
        var rx = new Regex("^" + Regex.Escape(name) + @"(\d+)\.png$", RegexOptions.IgnoreCase);
        return Directory.EnumerateFiles(dir, name + "*.png")
            .Select(f => (f, m: rx.Match(Path.GetFileName(f))))
            .Where(t => t.m.Success)
            .OrderBy(t => int.Parse(t.m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Select(t => t.f).ToList();
    }

    /// <summary>Side-by-side A | B | difference heat map, plus change statistics. Images of different sizes are compared at the top-left.</summary>
    public static (Bitmap Figure, JsonObject Stats) Compare(Bitmap a, Bitmap b, int tolerance)
    {
        int w = Math.Max(a.Width, b.Width), h = Math.Max(a.Height, b.Height);
        var pa = Pixels(a);
        var pb = Pixels(b);
        var diff = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var dpx = new int[w * h];
        int changed = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        double sum = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int ca = x < a.Width && y < a.Height ? pa[y * a.Width + x] : 0;
                int cb = x < b.Width && y < b.Height ? pb[y * b.Width + x] : 0;
                int d = 0;
                for (int sh = 0; sh < 32; sh += 8) d = Math.Max(d, Math.Abs(((ca >> sh) & 255) - ((cb >> sh) & 255)));
                sum += d;
                int gray = ((ca >> 16 & 255) + (ca >> 8 & 255) + (ca & 255)) / 12;
                if (d > tolerance)
                {
                    changed++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    int v = 96 + d * 159 / 255;
                    dpx[y * w + x] = unchecked((int)0xFF000000) | (v << 16) | ((v / 4) << 8);
                }
                else dpx[y * w + x] = unchecked((int)0xFF000000) | (gray << 16) | (gray << 8) | gray;
            }
        var data = diff.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(dpx, y * w, data.Scan0 + y * data.Stride, w);
        diff.UnlockBits(data);
        var stats = new JsonObject
        {
            ["size_a"] = $"{a.Width}x{a.Height}", ["size_b"] = $"{b.Width}x{b.Height}",
            ["changed_pixels"] = changed, ["changed_pct"] = R(100.0 * changed / Math.Max(1, w * h), 3),
            ["mean_channel_difference"] = R(sum / Math.Max(1, w * h), 2), ["tolerance"] = tolerance
        };
        if (changed > 0) stats["changed_bounds"] = new JsonObject { ["x"] = minX, ["y"] = minY, ["width"] = maxX - minX + 1, ["height"] = maxY - minY + 1 };
        using (diff)
        {
            var fig = Sheet(new[] { (a, "A"), (b, "B"), (diff, "difference (red = changed)") }, 1568, "checker", 3);
            return (fig, stats);
        }
    }

    // ---- game window ----

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

    /// <summary>Captures the running Celeste window's client area (works when it is covered; not when minimized).</summary>
    public static Bitmap CaptureGameWindow()
    {
        var proc = Process.GetProcessesByName("Celeste").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
                   ?? throw new ToolException("No Celeste window found. Start the game first.");
        IntPtr hwnd = proc.MainWindowHandle;
        if (IsIconic(hwnd)) throw new ToolException("The Celeste window is minimized; restore it to take a screenshot.");
        GetClientRect(hwnd, out var rc);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) throw new ToolException("The Celeste window has no visible area.");
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        bool ok;
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            ok = PrintWindow(hwnd, hdc, 3); // PW_CLIENTONLY | PW_RENDERFULLCONTENT (needed for D3D/GL surfaces)
            g.ReleaseHdc(hdc);
        }
        if (!ok || IsBlank(bmp))
        {
            var pt = new POINT();
            ClientToScreen(hwnd, ref pt);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(pt.X, pt.Y, 0, 0, new Size(w, h));
        }
        return bmp;
    }

    private static bool IsBlank(Bitmap bmp)
    {
        for (int i = 0; i < 64; i++)
        {
            var c = bmp.GetPixel(bmp.Width * (i % 8 * 2 + 1) / 16, bmp.Height * (i / 8 * 2 + 1) / 16);
            if (c.R + c.G + c.B > 0) return false;
        }
        return true;
    }
}
