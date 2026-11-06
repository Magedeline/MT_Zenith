using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

// Media tools: let the model see images/sprites/GIFs, hear and measure audio, skim video and screenshot the game.
public sealed partial class MapTools
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", "UnityMapEditor", "Library", "node_modules", "packages" };

    private static JsonObject BackgroundP => Enum("Backdrop behind transparent pixels (default checker).", "checker", "black", "white", "magenta", "none");

    private void RegisterMedia()
    {
        Add("list_media", "Find image, audio and video files (and FMOD .bank files) in the mod (or any folder), with sizes. " +
            "Use the paths with view_image / analyze_audio / analyze_video.", true,
            Props(("folder", P("string", "Folder to search, absolute or relative to the mod folder (default: the mod folder).")),
                  ("kind", Enum("Only this kind.", "image", "audio", "video", "bank")),
                  ("filter", P("string", "Substring the relative path must contain (e.g. 'characters/player' or 'boss').")),
                  ("limit", P("integer", "Max files listed (default 100)."))), None, ListMedia);
        Add("view_image", "Look at an image: PNG/JPG/BMP/GIF/WebP. Small pixel art is enlarged with nearest-neighbour so single pixels are " +
            "visible; transparency shows on a checkerboard. Also returns pixel statistics (size, colours, palette, transparency, content bounds). " +
            "'path' can also be a folder (contact sheet of its images), a sprite frame prefix such as 'characters/player/idle' " +
            "(all idle00.png, idle01.png, ... as a frame sheet), or an animated GIF (frame sheet unless 'frame' is given).", true,
            Props(("path", P("string", "Image file, folder or frame prefix; absolute or relative to the mod folder, Graphics/Atlases/Gameplay or Graphics.")),
                  ("frame", P("integer", "Frame index of an animated GIF or frame sequence to show alone.")),
                  ("x", P("integer", "Crop left (source pixels).")), ("y", P("integer", "Crop top.")),
                  ("width", P("integer", "Crop width.")), ("height", P("integer", "Crop height.")),
                  ("scale", P("number", "Display scale (default: automatic).")),
                  ("max_size", P("integer", "Longest side of the returned image in px (default 1568).")),
                  ("background", BackgroundP),
                  ("analyze", P("boolean", "Include pixel statistics (default true).")),
                  ("limit", P("integer", "Max images in a folder/frame sheet (default 48)."))), new[] { "path" }, ViewImage);
        Add("compare_images", "Compare two images: A | B | difference heat map side by side, plus changed-pixel statistics and bounds.", true,
            Props(("path_a", P("string", "First image.")), ("path_b", P("string", "Second image.")),
                  ("tolerance", P("integer", "Per-channel difference ignored (0-255, default 0)."))), new[] { "path_a", "path_b" }, CompareImages);
        Add("analyze_audio", "Analyze an audio file (wav; ogg/mp3/flac/m4a/... and video soundtracks need ffmpeg): duration, peak/RMS loudness, " +
            "clipping, silence and gaps, loudness timeline, spectrum balance and dominant pitch, stereo width, tempo estimate and loop seam, " +
            "plus a waveform + spectrogram image. With include_audio the clip itself is attached (16-bit mono WAV) for models that can listen.", true,
            Props(("path", P("string", "Audio file; absolute or relative to the mod folder or Audio/.")),
                  ("start", P("number", "Start time in seconds (default 0).")),
                  ("duration", P("number", "Seconds to analyze (default 180, max 900).")),
                  ("image", P("boolean", "Include the waveform/spectrogram image (default true).")),
                  ("include_audio", P("boolean", "Attach the audio clip as MCP audio content (default false).")),
                  ("clip_seconds", P("number", "Length of the attached clip (default 20, max 60)."))), new[] { "path" }, AnalyzeAudio);
        Add("analyze_video", "Skim a video (needs ffmpeg): container/codec/resolution/fps summary, a contact sheet of frames (evenly spaced, " +
            "at scene changes, or at given times) and an analysis of the soundtrack.", true,
            Props(("path", P("string", "Video file; absolute or relative to the mod folder.")),
                  ("frames", P("integer", "Number of frames (default 9, max 36).")),
                  ("mode", Enum("even (default) = evenly spaced; scenes = first frame + frames at scene cuts.", "even", "scenes")),
                  ("scene_threshold", P("number", "Scene-change sensitivity 0..1 (default 0.3; lower = more cuts).")),
                  ("start", P("number", "Start of the range in seconds (default 0).")),
                  ("end", P("number", "End of the range in seconds (default: the end).")),
                  ("times", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["description"] = "Exact timestamps (s) to grab instead." }),
                  ("width", P("integer", "Frame width in px (default 480).")),
                  ("separate", P("boolean", "Return each frame as its own image instead of one contact sheet (default false).")),
                  ("audio", P("boolean", "Analyze the soundtrack too (default true)."))), new[] { "path" }, AnalyzeVideo);
        Add("game_screenshot", "Screenshot the running Celeste window (combine with game_teleport to look at a room in-game).", true,
            Props(("max_size", P("integer", "Longest side in px (default 1280).")),
                  ("analyze", P("boolean", "Include pixel statistics (default false)."))), None, GameScreenshot);
    }

    // ---- paths ----

    private string? MediaRoot() => _host.Invoke(() => ModDir(_host.Path));

    private IEnumerable<string> MediaBases()
    {
        if (MediaRoot() is { } mod)
        {
            yield return mod;
            yield return Path.Combine(mod, "Graphics", "Atlases", "Gameplay");
            yield return Path.Combine(mod, "Graphics", "Atlases");
            yield return Path.Combine(mod, "Graphics");
            yield return Path.Combine(mod, "Audio");
        }
        if (GameAssets.CelesteDir is { } cel) yield return cel;
    }

    /// <summary>An existing file or folder (when <paramref name="allowDir"/>), trying the mod's media folders for relative paths.</summary>
    private string? TryResolveMedia(string path, bool allowDir)
    {
        path = path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        bool Ok(string p) => File.Exists(p) || (allowDir && Directory.Exists(p));
        if (Path.IsPathRooted(path)) return Ok(path) ? Path.GetFullPath(path) : null;
        foreach (string b in MediaBases())
        {
            string p = Path.GetFullPath(Path.Combine(b, path));
            if (Ok(p)) return p;
            if (!Path.HasExtension(path) && File.Exists(p + ".png")) return p + ".png";
        }
        return null;
    }

    private string ResolveMedia(string path, bool allowDir = false) =>
        TryResolveMedia(path, allowDir) ?? throw new ToolException($"File not found: '{path}'. Use an absolute path or one relative to the mod folder (list_media shows paths).");

    private string Rel(string full) => MediaRoot() is { } mod && full.StartsWith(mod, StringComparison.OrdinalIgnoreCase)
        ? Path.GetRelativePath(mod, full).Replace('\\', '/') : full;

    private static Rectangle? Crop(JsonObject a, Bitmap img)
    {
        if (I(a, "x") == null && I(a, "y") == null && I(a, "width") == null && I(a, "height") == null) return null;
        int x = I(a, "x") ?? 0, y = I(a, "y") ?? 0;
        return new Rectangle(x, y, I(a, "width") ?? img.Width - x, I(a, "height") ?? img.Height - y);
    }

    // ---- tools ----

    private ToolResult ListMedia(JsonObject a)
    {
        string root = S(a, "folder") is { Length: > 0 } f ? ResolveMedia(f, true) : MediaRoot() ?? throw new ToolException("No mod folder known; pass 'folder'.");
        if (!Directory.Exists(root)) throw new ToolException("Not a folder: " + root);
        string? kind = S(a, "kind"), filter = S(a, "filter")?.Replace('\\', '/');
        int limit = Math.Clamp(I(a, "limit") ?? 100, 1, 2000);
        var counts = new Dictionary<string, int>();
        var files = new JsonArray();
        int matched = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                    if (!SkipDirs.Contains(Path.GetFileName(sub))) stack.Push(sub);
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (Media.KindOf(file) is not { } k) continue;
                    counts[k] = counts.GetValueOrDefault(k) + 1;
                    if (kind is { Length: > 0 } && k != kind) continue;
                    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (!string.IsNullOrEmpty(filter) && !rel.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    matched++;
                    if (files.Count < limit)
                        files.Add(new JsonObject { ["path"] = Rel(file), ["kind"] = k, ["kb"] = Math.Round(new FileInfo(file).Length / 1024.0, 1) });
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        var sorted = new JsonArray(files.OrderBy(n => n!["path"]!.ToString(), StringComparer.OrdinalIgnoreCase).Select(n => n!.DeepClone()).ToArray());
        return ToolResult.Json(new JsonObject
        {
            ["folder"] = root, ["totals_by_kind"] = new JsonObject(counts.OrderBy(c => c.Key).Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value))),
            ["matched"] = matched, ["files"] = sorted,
            ["ffmpeg"] = Media.Ffmpeg != null ? "available (ogg/mp3/flac, video and webp supported)" : "not found: only wav audio and GDI+ images. " + Media.NoFfmpeg
        });
    }

    private ToolResult ViewImage(JsonObject a)
    {
        string arg = Req(a, "path");
        string bg = S(a, "background") is { Length: > 0 } b ? b : "checker";
        int maxSize = Math.Clamp(I(a, "max_size") ?? 1568, 64, 4096);
        int limit = Math.Clamp(I(a, "limit") ?? 48, 1, 200);
        int? frame = I(a, "frame");
        bool analyze = B(a, "analyze", true);

        string? path = TryResolveMedia(arg, true);
        if (path != null && Directory.Exists(path))
        {
            var images = Directory.EnumerateFiles(path).Where(f => Media.KindOf(f) == "image")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (images.Count == 0) throw new ToolException("No images in " + path);
            return SheetResult(images.Take(limit).ToList(), images.Count, Path.GetFileName, bg, maxSize, $"folder {Rel(path)}");
        }
        if (path == null)
        {
            // Celeste frame prefix: characters/player/idle -> idle00.png, idle01.png, ...
            var seq = MediaBases().Select(bp => Media.SequenceFrames(Path.GetFullPath(Path.Combine(bp, arg.Trim().Replace('/', Path.DirectorySeparatorChar)))))
                .FirstOrDefault(s => s.Count > 0);
            if (seq == null) ResolveMedia(arg);
            if (frame is int fi)
            {
                if (fi < 0 || fi >= seq!.Count) throw new ToolException($"frame must be 0..{seq!.Count - 1}.");
                path = seq[fi];
            }
            else
            {
                string prefix = Path.GetFileName(arg.TrimEnd('/', '\\'));
                return SheetResult(seq!.Take(limit).ToList(), seq!.Count, f => Path.GetFileNameWithoutExtension(f)[prefix.Length..], bg, maxSize,
                    $"frame sequence {arg} ({seq!.Count} frames; at Celeste's usual 0.08-0.1 s per frame ≈ {seq.Count * 0.09:0.0} s)");
            }
        }

        var frames = Media.LoadImageFrames(path, frame is int f0 ? f0 + 1 : limit, out var delays, out int total);
        try
        {
            var r = new ToolResult();
            var info = new JsonObject { ["path"] = Rel(path), ["file_kb"] = Math.Round(new FileInfo(path).Length / 1024.0, 1) };
            if (total > 1 && frame == null)
            {
                info["frames"] = total;
                info["delays_ms"] = new JsonArray(delays.Select(d => (JsonNode)d).ToArray());
                info["loop_ms"] = delays.Sum();
                if (frames.Count < total) info["shown"] = frames.Count;
                var cells = frames.Select((bm, i) => (bm, $"#{i}  {delays[i]}ms")).ToList();
                using var sheet = Media.Sheet(cells, maxSize, bg);
                if (analyze) info["frame0"] = Media.ImageStats(frames[0]);
                return r.AddPng(Media.Png(sheet)).AddText(info.ToJsonString(JsonOut));
            }
            int idx = frame ?? 0;
            if (idx < 0 || idx >= frames.Count) throw new ToolException($"frame must be 0..{total - 1}.");
            var img = frames[idx];
            if (total > 1) { info["frame"] = idx; info["frames"] = total; }
            var crop = Crop(a, img);
            using var view = Media.PrepareForView(img, crop, D(a, "scale"), maxSize, bg, out double used);
            info["shown_scale"] = Math.Round(used, 3);
            if (crop is { } c) info["crop"] = $"{c.X},{c.Y} {c.Width}x{c.Height}";
            if (analyze)
            {
                if (crop is { } cr)
                {
                    using var part = img.Clone(Rectangle.Intersect(cr, new Rectangle(0, 0, img.Width, img.Height)), img.PixelFormat);
                    info["stats"] = Media.ImageStats(part);
                }
                else info["stats"] = Media.ImageStats(img);
            }
            return r.AddPng(Media.Png(view)).AddText(info.ToJsonString(JsonOut));
        }
        finally { foreach (var f in frames) f.Dispose(); }
    }

    private ToolResult SheetResult(List<string> files, int total, Func<string, string> label, string bg, int maxSize, string title)
    {
        var cells = new List<(Bitmap, string)>();
        var list = new JsonArray();
        try
        {
            foreach (string f in files)
            {
                try
                {
                    var frames = Media.LoadImageFrames(f, 1, out _, out int n);
                    cells.Add((frames[0], label(f)));
                    var item = new JsonObject { ["name"] = label(f), ["size"] = $"{frames[0].Width}x{frames[0].Height}" };
                    if (n > 1) item["frames"] = n;
                    list.Add(item);
                }
                catch (ToolException) { list.Add(new JsonObject { ["name"] = label(f), ["error"] = "undecodable" }); }
            }
            using var sheet = Media.Sheet(cells, maxSize, bg);
            var info = new JsonObject { ["source"] = title, ["count"] = total, ["shown"] = cells.Count, ["items"] = list };
            return new ToolResult().AddPng(Media.Png(sheet)).AddText(info.ToJsonString(JsonOut));
        }
        finally { foreach (var (bm, _) in cells) bm.Dispose(); }
    }

    private ToolResult CompareImages(JsonObject a)
    {
        string pa = ResolveMedia(Req(a, "path_a")), pb = ResolveMedia(Req(a, "path_b"));
        var fa = Media.LoadImageFrames(pa, 1, out _, out _);
        var fb = Media.LoadImageFrames(pb, 1, out _, out _);
        try
        {
            var (fig, stats) = Media.Compare(fa[0], fb[0], Math.Clamp(I(a, "tolerance") ?? 0, 0, 255));
            using (fig)
            {
                stats["a"] = Rel(pa);
                stats["b"] = Rel(pb);
                return new ToolResult().AddPng(Media.Png(fig)).AddText(stats.ToJsonString(JsonOut));
            }
        }
        finally { fa[0].Dispose(); fb[0].Dispose(); }
    }

    private ToolResult AnalyzeAudio(JsonObject a)
    {
        string path = ResolveMedia(Req(a, "path"));
        double start = Math.Max(0, D(a, "start") ?? 0), dur = Math.Clamp(D(a, "duration") ?? 180, 0.05, 900);
        var pcm = Media.LoadAudio(path, start, dur);
        var stats = Media.AudioStats(pcm);
        stats["path"] = Rel(path);
        if (pcm.SourceDuration > pcm.Start + pcm.Duration + 0.05) stats["truncated"] = $"only {pcm.Duration:0.##}s of {pcm.SourceDuration:0.##}s analyzed; use start/duration for the rest";
        var r = new ToolResult();
        if (B(a, "image", true) && pcm.Length > 0)
        {
            using var fig = Media.AudioFigure(pcm, $"{Path.GetFileName(path)}  {pcm.Start:0.##}-{pcm.Start + pcm.Duration:0.##}s  peak {stats["peak_dbfs"]} dBFS, RMS {stats["rms_dbfs"]} dBFS");
            r.AddPng(Media.Png(fig));
        }
        if (B(a, "include_audio") && pcm.Length > 0)
        {
            double clip = Math.Clamp(D(a, "clip_seconds") ?? 20, 0.5, 60);
            r.AddAudio(Media.WavClip(pcm, clip), "audio/wav");
            stats["attached_audio"] = $"{Math.Min(clip, pcm.Duration):0.##}s mono WAV from {pcm.Start:0.##}s";
        }
        return r.AddText(stats.ToJsonString(JsonOut));
    }

    private ToolResult AnalyzeVideo(JsonObject a)
    {
        string path = ResolveMedia(Req(a, "path"));
        if (Media.Ffmpeg == null) throw new ToolException(Media.NoFfmpeg);
        var probe = Media.Probe(path);
        var summary = Media.Summary(probe);
        summary["path"] = Rel(path);
        if (Media.FirstStream(probe, "video") == null) throw new ToolException("No video stream; use analyze_audio for audio files.");
        double length = Media.DurationOf(probe);
        double start = Math.Clamp(D(a, "start") ?? 0, 0, Math.Max(0, length - 0.01));
        double end = D(a, "end") is double e && e > start ? Math.Min(e, length > 0 ? length : e) : length;
        int count = Math.Clamp(I(a, "frames") ?? 9, 1, 36);
        int width = Math.Clamp(I(a, "width") ?? 480, 64, 1920);

        List<(Bitmap Frame, double Time)> frames;
        if (a["times"] is JsonArray ta && ta.Count > 0)
            frames = Media.FramesAt(path, ta.Select(n => n is JsonValue v && v.TryGetValue(out double d) ? d : 0).Take(36), width);
        else if (S(a, "mode") == "scenes")
        {
            frames = Media.SceneFrames(path, Math.Clamp(D(a, "scene_threshold") ?? 0.3, 0.01, 1), count, width, start, end > start ? end : null);
            summary["scene_cuts_found"] = frames.Count - 1;
        }
        else frames = Media.FramesAt(path, Media.EvenTimes(start, end > start ? end : start + 1, count), width);

        var r = new ToolResult();
        try
        {
            if (B(a, "separate"))
                foreach (var (bm, _) in frames) r.AddPng(Media.Png(bm));
            else
            {
                using var sheet = Media.Sheet(frames.Select(f => (f.Frame, $"t={f.Time:0.##}s")).ToList(), 1568, "black");
                r.AddPng(Media.Png(sheet));
            }
            summary["frame_times_s"] = new JsonArray(frames.Select(f => (JsonNode)Math.Round(f.Time, 2)).ToArray());
        }
        finally { foreach (var (bm, _) in frames) bm.Dispose(); }

        if (B(a, "audio", true) && Media.FirstStream(probe, "audio") != null)
        {
            try
            {
                var pcm = Media.LoadAudio(path, start, Math.Min(Math.Max(end - start, 1), 300));
                var stats = Media.AudioStats(pcm);
                summary["soundtrack"] = stats;
                using var fig = Media.AudioFigure(pcm, $"soundtrack {pcm.Start:0.##}-{pcm.Start + pcm.Duration:0.##}s");
                r.AddPng(Media.Png(fig));
            }
            catch (ToolException ex) { summary["soundtrack"] = "could not analyze: " + ex.Message; }
        }
        return r.AddText(summary.ToJsonString(JsonOut));
    }

    private ToolResult GameScreenshot(JsonObject a)
    {
        using var shot = Media.CaptureGameWindow();
        using var view = Media.PrepareForView(shot, null, null, Math.Clamp(I(a, "max_size") ?? 1280, 64, 4096), "black", out double used);
        var info = new JsonObject { ["window"] = $"{shot.Width}x{shot.Height}", ["shown_scale"] = Math.Round(used, 3) };
        if (B(a, "analyze")) info["stats"] = Media.ImageStats(shot);
        return new ToolResult().AddPng(Media.Png(view)).AddText(info.ToJsonString(JsonOut));
    }
}
