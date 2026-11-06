using System.Globalization;
using System.Text.RegularExpressions;

namespace CELMapViewer.Agent;

// Video: frame extraction through ffmpeg (evenly spaced, at given times, or at scene changes).
public static partial class Media
{
    private static string ScaleFilter(int width) => $"scale='min({width},iw)':-2";

    /// <summary>One frame per timestamp (seconds), scaled to at most <paramref name="width"/> px wide.</summary>
    public static List<(Bitmap Frame, double Time)> FramesAt(string path, IEnumerable<double> times, int width)
    {
        string exe = Ffmpeg ?? throw new ToolException(NoFfmpeg);
        var list = new List<(Bitmap, double)>();
        string? lastErr = null;
        foreach (double t in times)
        {
            var (code, outp, err) = Run(exe, new[]
            {
                "-v", "error", "-nostdin", "-ss", F(Math.Max(0, t)), "-i", path, "-frames:v", "1", "-an",
                "-vf", ScaleFilter(width), "-f", "image2pipe", "-c:v", "png", "-"
            }, 60_000);
            var pngs = code == 0 ? SplitPngs(outp) : new List<byte[]>();
            if (pngs.Count == 0) { lastErr = err.Trim(); continue; }
            list.Add((FromPng(pngs[0]), t));
        }
        if (list.Count == 0) throw new ToolException("ffmpeg could not extract any frame" + (string.IsNullOrEmpty(lastErr) ? "." : ": " + lastErr));
        return list;
    }

    /// <summary>Evenly spaced timestamps (frame centres) over [start, end).</summary>
    public static List<double> EvenTimes(double start, double end, int count)
    {
        var t = new List<double>();
        double span = Math.Max(0, end - start);
        for (int i = 0; i < count; i++) t.Add(start + span * (i + 0.5) / count);
        return t;
    }

    private static readonly Regex PtsTime = new(@"pts_time:\s*([0-9.]+)", RegexOptions.Compiled);

    /// <summary>The first frame plus frames where the scene-change score exceeds <paramref name="threshold"/> (0..1).</summary>
    public static List<(Bitmap Frame, double Time)> SceneFrames(string path, double threshold, int max, int width, double start, double? end)
    {
        string exe = Ffmpeg ?? throw new ToolException(NoFfmpeg);
        var result = FramesAt(path, new[] { start }, width);
        var args = new List<string> { "-hide_banner", "-v", "info", "-nostdin", "-ss", F(start) };
        if (end is { } e && e > start) { args.Add("-t"); args.Add(F(e - start)); }
        args.AddRange(new[]
        {
            "-i", path, "-an", "-vf", $"select='gt(scene,{threshold.ToString("0.###", CultureInfo.InvariantCulture)})',showinfo,{ScaleFilter(width)}",
            "-fps_mode", "vfr", "-frames:v", Math.Max(1, max - 1).ToString(CultureInfo.InvariantCulture), "-f", "image2pipe", "-c:v", "png", "-"
        });
        var (code, outp, err) = Run(exe, args, 300_000);
        if (code != 0) throw new ToolException("ffmpeg scene detection failed: " + err.Trim().Split('\n').LastOrDefault());
        var pngs = SplitPngs(outp);
        var times = PtsTime.Matches(err).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        for (int i = 0; i < pngs.Count; i++)
        {
            double t = start + (i < times.Count ? times[i] : 0);
            if (t - start < 0.05) continue;
            result.Add((FromPng(pngs[i]), t));
        }
        return result;
    }
}
