using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CELMapViewer;

/// <summary>A GameBanana mod as listed by Maddie480's mod search database.</summary>
public sealed record BananaMod(string Name, string Author, string PageUrl, string Category);

/// <summary>One file of the Celeste Community Asset Drive (maddie480.ovh/celeste/asset-drive).</summary>
public sealed class DriveAsset
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("readme")] public string? Readme { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("template")] public string? Template { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonIgnore] public string Category { get; set; } = "";

    /// <summary>The author with the drive's "(give credit)" style suffixes removed.</summary>
    [JsonIgnore] public string CleanAuthor => MaddieDb.CleanAuthor(Author);
    /// <summary>True when the drive folder says no credit is needed (still credited by MT_Zenith, but marked optional).</summary>
    [JsonIgnore] public bool CreditOptional =>
        (Author + " " + Name).Contains("no credit", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string FileUrl => MaddieDb.Base + "/celeste/asset-drive/files/" + Id;
    public override string ToString() => $"{Name}  —  {CleanAuthor}";
}

/// <summary>
/// Data from Maddie480's Celeste services (https://maddie480.ovh): the Everest update list and mod search database
/// (to credit installed mods with their GameBanana page and author) and the Community Asset Drive (credited
/// community decals, stylegrounds, tilesets, ...). Everything is cached under the data folder, refreshed daily,
/// and the stale cache is used when offline.
/// </summary>
public static class MaddieDb
{
    public const string Base = "https://maddie480.ovh";
    public const string AssetDriveUrl = Base + "/celeste/asset-drive";
    public static readonly string[] DriveCategories = { "decals", "stylegrounds", "fgtilesets", "bgtilesets", "misc", "hires" };
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Dictionary<string, BananaMod>? _byEverestName;
    private static readonly Dictionary<string, List<DriveAsset>> Drive = new(StringComparer.OrdinalIgnoreCase);

    public static string CacheDir => Path.Combine(AppInfo.DataDir, "cache", "maddie480");

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"MT_Zenith/{AppInfo.Version} (Celeste map editor)");
        return h;
    }

    /// <summary>Strips "(give credit)", "(Notify - Credit)" and similar suffixes from an Asset Drive author folder.</summary>
    public static string CleanAuthor(string author)
    {
        string a = System.Text.RegularExpressions.Regex.Replace(author, @"\s*\((?:[^)]*\b(?:credit|notify|ask)\b[^)]*)\)\s*", " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return a.Length == 0 ? author : a;
    }

    /// <summary>A cached download; refreshes it when older than a day (or when forced), falls back to the cache on failure.</summary>
    private static async Task<string?> CachedAsync(string url, string file, bool force, CancellationToken ct)
    {
        string path = Path.Combine(CacheDir, file);
        bool fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge;
        if (!fresh || force)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string text = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
                string tmp = path + ".tmp";
                await File.WriteAllTextAsync(tmp, text, ct).ConfigureAwait(false);
                File.Move(tmp, path, true);
                return text;
            }
            catch (Exception) when (File.Exists(path) && !ct.IsCancellationRequested) { /* offline: use the stale copy */ }
        }
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
    }

    public static DateTime? CacheTime(string file)
    {
        string path = Path.Combine(CacheDir, file);
        return File.Exists(path) ? File.GetLastWriteTime(path) : null;
    }

    // ---- installed mods → GameBanana ----

    /// <summary>Everest mod name → GameBanana mod (page, author). Empty when never downloaded and offline.</summary>
    public static async Task<IReadOnlyDictionary<string, BananaMod>> ModsAsync(bool refresh = false, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_byEverestName != null && !refresh) return _byEverestName;
            string? upd = await CachedAsync(Base + "/celeste/everest_update.yaml", "everest_update.yaml", refresh, ct).ConfigureAwait(false);
            string? search = await CachedAsync(Base + "/celeste/mod_search_database.yaml", "mod_search_database.yaml", refresh, ct).ConfigureAwait(false);
            var byFile = search == null ? new() : ParseSearchDatabase(search);
            var result = new Dictionary<string, BananaMod>(StringComparer.OrdinalIgnoreCase);
            if (upd != null)
                foreach (var (name, fileId) in ParseUpdateList(upd))
                    if (byFile.TryGetValue(fileId, out var mod)) result.TryAdd(name, mod);
            return _byEverestName = result;
        }
        finally { Gate.Release(); }
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && (v[0] == '\'' && v[^1] == '\'' || v[0] == '"' && v[^1] == '"'))
            v = v[1..^1].Replace("''", "'");
        return v;
    }

    /// <summary>everest_update.yaml: top-level "ModName:" blocks with a "MirrorName:" (GameBanana file id) → (name, file id).</summary>
    public static List<(string Name, string FileId)> ParseUpdateList(string yaml)
    {
        var list = new List<(string, string)>();
        string? current = null;
        foreach (string raw in yaml.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] != ' ' && line.EndsWith(':')) { current = Unquote(line[..^1]); continue; }
            string t = line.TrimStart();
            if (current != null && t.StartsWith("MirrorName:", StringComparison.Ordinal))
                list.Add((current.Trim(), Unquote(t["MirrorName:".Length..])));
        }
        return list;
    }

    /// <summary>mod_search_database.yaml: GameBanana file id → the mod it belongs to.</summary>
    public static Dictionary<string, BananaMod> ParseSearchDatabase(string yaml)
    {
        var byFile = new Dictionary<string, BananaMod>(StringComparer.Ordinal);
        string name = "", author = "", page = "", category = "", section = "";
        var files = new List<string>();
        void Flush()
        {
            if (name.Length > 0)
            {
                var m = new BananaMod(name, author, page, category);
                foreach (string f in files) byFile.TryAdd(f, m);
            }
            name = author = page = category = section = "";
            files.Clear();
        }
        foreach (string raw in yaml.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("- ", StringComparison.Ordinal)) { Flush(); line = "  " + line[2..]; }
            int indent = line.Length - line.TrimStart(' ').Length;
            string t = line.Trim();
            int colon = t.IndexOf(':');
            if (colon <= 0) continue;
            string key = t[..colon], val = t[(colon + 1)..];
            if (indent == 2 && key.StartsWith('-')) { key = key.TrimStart('-', ' '); indent = 4; }
            if (indent == 2)
            {
                section = key;
                switch (key)
                {
                    case "Name": name = Unquote(val); break;
                    case "Author": author = Unquote(val); break;
                    case "PageURL": page = Unquote(val); break;
                }
            }
            else if (section == "Files" && key == "MirrorName") files.Add(Unquote(val));
            else if (section == "Category" && indent == 4 && key == "Name") category = Unquote(val);
        }
        Flush();
        return byFile;
    }

    // ---- Community Asset Drive ----

    /// <summary>Every file in one Asset Drive category (decals, stylegrounds, fgtilesets, bgtilesets, misc, hires).</summary>
    public static async Task<List<DriveAsset>> DriveListAsync(string category, bool refresh = false, CancellationToken ct = default)
    {
        lock (Drive)
            if (!refresh && Drive.TryGetValue(category, out var have)) return have;
        string? json = await CachedAsync($"{Base}/celeste/asset-drive/list/{category}", $"asset-drive-{category}.json", refresh, ct).ConfigureAwait(false);
        var list = json == null ? new List<DriveAsset>() : JsonSerializer.Deserialize<List<DriveAsset>>(json) ?? new();
        foreach (var a in list) a.Category = category;
        lock (Drive) Drive[category] = list;
        return list;
    }

    /// <summary>All Asset Drive categories (for matching files in the user's mod to their authors).</summary>
    public static async Task<List<DriveAsset>> DriveAllAsync(bool refresh = false, CancellationToken ct = default)
    {
        var all = new List<DriveAsset>();
        foreach (string c in DriveCategories)
        {
            try { all.AddRange(await DriveListAsync(c, refresh, ct).ConfigureAwait(false)); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* one category failing is fine */ }
        }
        return all;
    }

    /// <summary>A drive file's bytes (cached on disk by id).</summary>
    public static async Task<byte[]> DriveFileAsync(string id, CancellationToken ct = default)
    {
        if (id.Length == 0 || id.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-')) throw new ArgumentException("Bad asset id.");
        string path = Path.Combine(CacheDir, "files", id);
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        byte[] data = await Http.GetByteArrayAsync($"{Base}/celeste/asset-drive/files/{id}", ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data, ct).ConfigureAwait(false);
        return data;
    }

    /// <summary>A drive readme/notes text file, or null.</summary>
    public static async Task<string?> DriveTextAsync(string? id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try { return System.Text.Encoding.UTF8.GetString(await DriveFileAsync(id, ct).ConfigureAwait(false)); }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }
}
