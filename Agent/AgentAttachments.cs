using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

/// <summary>
/// Turns files attached in the agent chat into ACP prompt content: images (and audio, when the agent accepts it) inline,
/// plus figures and statistics as text so text-only agents still get the analysis, and a resource link to the file.
/// </summary>
public static class AgentAttachments
{
    private const int MaxSide = 1568;
    private const double AudioClipSeconds = 30;

    public static JsonArray Build(IReadOnlyList<string> files, bool images, bool audio)
    {
        var blocks = new JsonArray();
        foreach (string file in files)
        {
            string kind = Media.KindOf(file) ?? "file";
            var note = new System.Text.StringBuilder($"Attached {kind}: {file}");
            try
            {
                switch (kind)
                {
                    case "image": AddImage(file, images, blocks, note); break;
                    case "audio": AddAudio(file, images, audio, blocks, note); break;
                    case "video": AddVideo(file, images, blocks, note); break;
                    case "bank": note.Append("\n(FMOD bank: not decodable here; ask for the source .wav/.ogg.)"); break;
                }
            }
            catch (Exception ex) { note.Append($"\n(could not analyze: {ex.Message})"); }
            note.Append(kind switch
            {
                "image" => "\nFor more detail call the view_image tool with this path (crop x/y/width/height, frame, compare_images).",
                "audio" => "\nFor more detail call the analyze_audio tool with this path (start/duration, include_audio).",
                "video" => "\nFor more detail call the analyze_video tool with this path (times, mode=scenes, frames).",
                _ => ""
            });
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = note.ToString() });
            blocks.Add(new JsonObject
            {
                ["type"] = "resource_link", ["uri"] = new Uri(file).AbsoluteUri, ["name"] = Path.GetFileName(file), ["mimeType"] = Media.MimeOf(file),
                ["size"] = new FileInfo(file).Length
            });
        }
        return blocks;
    }

    private static JsonObject Image(byte[] png) => new() { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(png) };

    private static void AddImage(string file, bool images, JsonArray blocks, System.Text.StringBuilder note)
    {
        var frames = Media.LoadImageFrames(file, 1, out _, out int total);
        try
        {
            var stats = Media.ImageStats(frames[0]);
            if (total > 1) stats["frames"] = total;
            if (images)
            {
                using var view = Media.PrepareForView(frames[0], null, null, MaxSide, "checker", out double s);
                blocks.Add(Image(Media.Png(view)));
                stats["shown_scale"] = Math.Round(s, 3);
            }
            note.Append("\n").Append(stats.ToJsonString(MapTools.JsonOut));
        }
        finally { frames[0].Dispose(); }
    }

    private static void AddAudio(string file, bool images, bool audio, JsonArray blocks, System.Text.StringBuilder note)
    {
        var pcm = Media.LoadAudio(file, 0, 300);
        var stats = Media.AudioStats(pcm);
        if (images && pcm.Length > 0)
        {
            using var fig = Media.AudioFigure(pcm, Path.GetFileName(file));
            blocks.Add(Image(Media.Png(fig)));
        }
        if (audio && pcm.Length > 0)
        {
            blocks.Add(new JsonObject { ["type"] = "audio", ["mimeType"] = "audio/wav", ["data"] = Convert.ToBase64String(Media.WavClip(pcm, AudioClipSeconds)) });
            stats["attached_audio"] = $"first {Math.Min(AudioClipSeconds, pcm.Duration):0.##}s as mono WAV";
        }
        note.Append("\n").Append(stats.ToJsonString(MapTools.JsonOut));
    }

    private static void AddVideo(string file, bool images, JsonArray blocks, System.Text.StringBuilder note)
    {
        if (Media.Ffmpeg == null) { note.Append("\n(").Append(Media.NoFfmpeg).Append(')'); return; }
        var probe = Media.Probe(file);
        var summary = Media.Summary(probe);
        double len = Media.DurationOf(probe);
        if (images && Media.FirstStream(probe, "video") != null)
        {
            var frames = Media.FramesAt(file, Media.EvenTimes(0, len > 0 ? len : 1, 9), 480);
            try
            {
                using var sheet = Media.Sheet(frames.Select(f => (f.Frame, $"t={f.Time:0.##}s")).ToList(), MaxSide, "black");
                blocks.Add(Image(Media.Png(sheet)));
                summary["frame_times_s"] = new JsonArray(frames.Select(f => (JsonNode)Math.Round(f.Time, 2)).ToArray());
            }
            finally { foreach (var (bm, _) in frames) bm.Dispose(); }
        }
        if (Media.FirstStream(probe, "audio") != null)
            summary["soundtrack"] = Media.AudioStats(Media.LoadAudio(file, 0, Math.Clamp(len, 1, 300)));
        note.Append("\n").Append(summary.ToJsonString(MapTools.JsonOut));
    }
}
