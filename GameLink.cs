using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CELMapViewer;

public sealed record GameState(
    bool InLevel, string? Sid, string? Side, string? Room, string? Map,
    Rectangle Bounds, PointF? Player, bool Dead);

/// <summary>Talks to the running game through Everest's debug RC (MapEditorBridge endpoints in CEL).</summary>
public sealed class GameLink
{
    public string BaseUrl { get; set; } = "http://localhost:32270";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public async Task<GameState?> GetStateAsync()
    {
        try
        {
            string json = await _http.GetStringAsync(BaseUrl + "/cel/editor/state");
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("inLevel", out var il) || !il.GetBoolean())
                return new GameState(false, null, null, null, null, Rectangle.Empty, null, false);

            Rectangle bounds = Rectangle.Empty;
            if (r.TryGetProperty("bounds", out var b))
                bounds = new Rectangle(b[0].GetInt32(), b[1].GetInt32(), b[2].GetInt32(), b[3].GetInt32());
            PointF? player = null;
            if (r.TryGetProperty("player", out var p))
                player = new PointF(p[0].GetSingle(), p[1].GetSingle());

            return new GameState(true, S(r, "sid"), S(r, "side"), S(r, "room"), S(r, "map"), bounds, player,
                r.TryGetProperty("dead", out var d) && d.GetBoolean());
        }
        catch
        {
            return null;
        }
    }

    private static string? S(JsonElement r, string key) =>
        r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Loads the room; with <paramref name="local"/> the nearest spawn to it is used, or the exact point.</summary>
    public Task<string> TeleportAsync(string mapPath, string room, PointF? local = null, bool exact = false)
    {
        string url = $"{BaseUrl}/cel/editor/tp?map={Uri.EscapeDataString(mapPath)}&level={Uri.EscapeDataString(room)}";
        if (local is PointF p)
            url += "&x=" + p.X.ToString(CultureInfo.InvariantCulture) + "&y=" + p.Y.ToString(CultureInfo.InvariantCulture)
                 + (exact ? "&exact=1" : "");
        return GetTextAsync(url);
    }

    public Task<string> ReloadAsync(string sid, string side) =>
        GetTextAsync($"{BaseUrl}/cel/editor/reload?area={Uri.EscapeDataString(sid)}&side={side}");

    public Task<string> ReloadMapAsync(string mapPath) =>
        GetTextAsync($"{BaseUrl}/cel/editor/reload?map={Uri.EscapeDataString(mapPath)}");

    private async Task<string> GetTextAsync(string url)
    {
        try
        {
            using var resp = await _http.GetAsync(url);
            return (await resp.Content.ReadAsStringAsync()).Trim();
        }
        catch (Exception ex)
        {
            return "ERROR: game not reachable (" + ex.Message + ")";
        }
    }

    private static readonly Lazy<string?> _celesteDir = new(LocateCeleste);
    private static readonly Lazy<string?> _modDir = new(LocateMod);

    /// <summary>
    /// Celeste install root. Built from source, MT_Zenith lives in Celeste/Mods/CEL/Tools/CELMapViewer/bin/...;
    /// a downloaded release falls back to CELESTE_DIR, the "Set Celeste folder" setting, Steam, Olympus and common paths.
    /// </summary>
    public static string? FindCelesteDir() => _celesteDir.Value;

    /// <summary>The CEL mod folder: above the exe when built from source, else Celeste/Mods/CEL (or any loose mod folder named Celestellaris).</summary>
    public static string? FindModDir() => _modDir.Value;

    public static bool IsCelesteDir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "Celeste.dll")) && Directory.Exists(Path.Combine(dir, "Mods"));

    private static bool IsModDir(string dir) =>
        File.Exists(Path.Combine(dir, "everest.yaml")) && Directory.Exists(Path.Combine(dir, "Maps"));

    private static string? LocateCeleste()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (IsCelesteDir(dir.FullName)) return dir.FullName;
        // Prefer an install with vanilla content (tilesets/atlases); a stripped copy is only the fallback.
        string? fallback = null;
        foreach (string? c in CelesteCandidates())
            try
            {
                if (!IsCelesteDir(c)) continue;
                string full = Path.GetFullPath(c!);
                if (Directory.Exists(Path.Combine(full, "Content", "Graphics"))) return full;
                fallback ??= full;
            }
            catch { }
        return fallback;
    }

    private static string? LocateMod()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (IsModDir(dir.FullName)) return dir.FullName;
        if (FindCelesteDir() is not { } cel) return null;
        string mods = Path.Combine(cel, "Mods"), cel1 = Path.Combine(mods, "CEL");
        if (IsModDir(cel1)) return cel1;
        try
        {
            foreach (string d in Directory.EnumerateDirectories(mods))
                if (IsModDir(d) && File.ReadLines(Path.Combine(d, "everest.yaml")).Take(5).Any(l => l.Trim() == "Name: Celestellaris" || l.Trim() == "- Name: Celestellaris"))
                    return d;
        }
        catch { }
        return null;
    }

    private static IEnumerable<string?> CelesteCandidates()
    {
        yield return Environment.GetEnvironmentVariable("CELESTE_DIR");
        yield return AppSettings.Load().CelesteDir;

        // Steam: the default library, then every library listed in libraryfolders.vdf.
        string? steam = null;
        try { steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch { }
        steam ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        yield return Path.Combine(steam, "steamapps", "common", "Celeste");
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        List<string> libs = new();
        try
        {
            if (File.Exists(vdf))
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        catch { }
        foreach (string lib in libs) yield return Path.Combine(lib, "steamapps", "common", "Celeste");

        // Olympus (the Everest installer) remembers every install it manages.
        List<string> olympus = new();
        try
        {
            string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Olympus", "config.json");
            if (File.Exists(cfg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.TryGetProperty("installs", out var installs) && installs.ValueKind == JsonValueKind.Array)
                    foreach (var i in installs.EnumerateArray())
                        if (i.TryGetProperty("path", out var path) && path.GetString() is { } s)
                            olympus.Add(File.Exists(s) ? Path.GetDirectoryName(s)! : s);
            }
        }
        catch { }
        foreach (string o in olympus) yield return o;

        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            yield return Path.Combine(root, "Celeste");
            yield return Path.Combine(root, "Epic Games", "Celeste");
            yield return Path.Combine(root, "itch", "apps", "celeste");
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "itch", "apps", "celeste");
    }
    public static string LaunchGame()
    {
        string? dir = FindCelesteDir();
        if (dir == null) return "Celeste install not found: use File ▸ Set Celeste folder… (or set CELESTE_DIR).";
        string exe = Path.Combine(dir, "Celeste.exe");
        if (!File.Exists(exe)) return "Celeste.exe not found in " + dir;
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true });
        return "Starting Celeste…";
    }
}
