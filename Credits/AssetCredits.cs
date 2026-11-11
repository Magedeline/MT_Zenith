using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CELMapViewer;

public enum CreditOrigin
{
    /// <summary>From an installed mod (helper, collab, asset pack); credited with its GameBanana page and author.</summary>
    Mod,
    /// <summary>In the map's own mod and recorded as taken from someone (Asset Drive import or a manual credit).</summary>
    Borrowed,
    /// <summary>In the map's own mod and marked as the mapper's own work.</summary>
    Own,
    /// <summary>In the map's own mod with no credit recorded; matches an Asset Drive file by name.</summary>
    PossiblyBorrowed,
    /// <summary>In the map's own mod with no credit recorded at all.</summary>
    Unrecorded,
    /// <summary>Vanilla Celeste.</summary>
    Vanilla,
    /// <summary>Not found anywhere (missing mod or typo).</summary>
    Missing,
}

/// <summary>One credited thing: an asset or entity type and who it comes from, with how often the map uses it.</summary>
public sealed class CreditEntry
{
    public required string Kind;          // Decal, Styleground, Tileset, Entity, Trigger
    public required string Asset;         // texture path / entity name
    public required CreditOrigin Origin;
    public string CreditTo = "";          // author(s)
    public string Source = "";            // mod name, "Celeste Community Asset Drive", ...
    public string Url = "";
    public string Note = "";
    public string? File;                  // mod-relative file, for own-mod assets
    public int Uses;

    public bool NeedsAttention => Origin is CreditOrigin.PossiblyBorrowed or CreditOrigin.Unrecorded or CreditOrigin.Missing;
}

/// <summary>A credit the mapper recorded for a file in their own mod (stored in MT_Zenith.credits.json in the mod folder).</summary>
public sealed class RecordedCredit
{
    public string Path { get; set; } = "";        // mod-relative, forward slashes
    public bool Own { get; set; }                 // true = the mapper's own work
    public string Author { get; set; } = "";
    public string Source { get; set; } = "";      // "Celeste Community Asset Drive", a mod name, a website...
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";       // the author's terms (readme / notes), shortened
    public DateTime Added { get; set; } = DateTime.UtcNow;
}

/// <summary>The credit manifest of the mapper's own mod: who made each asset they brought in.</summary>
public sealed class CreditManifest
{
    public const string FileName = "MT_Zenith.credits.json";
    public List<RecordedCredit> Assets { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathFor(string modDir) => System.IO.Path.Combine(modDir, FileName);

    public static CreditManifest Load(string? modDir)
    {
        if (modDir == null) return new();
        try
        {
            string p = PathFor(modDir);
            if (File.Exists(p)) return JsonSerializer.Deserialize<CreditManifest>(File.ReadAllText(p)) ?? new();
        }
        catch { /* corrupt: start over rather than crash; the file is left alone until the next save */ }
        return new();
    }

    public void Save(string modDir) => File.WriteAllText(PathFor(modDir), JsonSerializer.Serialize(this, Json));

    public RecordedCredit? Find(string rel)
    {
        rel = Norm(rel);
        return Assets.FirstOrDefault(a => Norm(a.Path).Equals(rel, StringComparison.OrdinalIgnoreCase))
            // a credit on a folder covers everything in it (e.g. a whole decal pack)
            ?? Assets.Where(a => a.Path.EndsWith('/') && rel.StartsWith(Norm(a.Path), StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(a => a.Path.Length).FirstOrDefault();
    }

    public void Set(RecordedCredit c)
    {
        c.Path = Norm(c.Path) + (c.Path.EndsWith('/') ? "/" : "");
        Assets.RemoveAll(a => a.Path.Equals(c.Path, StringComparison.OrdinalIgnoreCase));
        Assets.Add(c);
    }

    private static string Norm(string p) => p.Replace('\\', '/').Trim('/');
}

/// <summary>Works out who to credit for everything a map uses, using installed mods and Maddie480's databases.</summary>
public static class AssetCredits
{
    public const string VanillaCredit = "Maddy Makes Games / Extremely OK Games";
    private const string Atlas = "Graphics/Atlases/Gameplay/";

    public sealed record ScanResult(List<CreditEntry> Entries, string? ModDir, string? ModName, bool OnlineData);

    /// <summary>
    /// Scans the map. Call off the UI thread: it reads mod zips and may download Maddie480's databases
    /// (cached; with <paramref name="online"/> false only the cache is used).
    /// </summary>
    public static async Task<ScanResult> ScanAsync(MapBin map, string? mapPath, bool online = true, bool refresh = false, CancellationToken ct = default)
    {
        string? ownDir = mapPath == null ? null : MapModel.ModDirOf(mapPath);
        string? ownFull = ownDir == null ? null : Path.GetFullPath(ownDir).TrimEnd('\\', '/');
        var manifest = CreditManifest.Load(ownDir);

        IReadOnlyDictionary<string, BananaMod> mods = new Dictionary<string, BananaMod>();
        List<DriveAsset> drive = new();
        bool gotOnline = false;
        if (online)
        {
            try { mods = await MaddieDb.ModsAsync(refresh, ct).ConfigureAwait(false); gotOnline = mods.Count > 0; }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            try { drive = await MaddieDb.DriveAllAsync(refresh, ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        var driveByName = new Dictionary<string, DriveAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drive) driveByName.TryAdd(StripPng(d.Name).Replace('\\', '/'), d);

        await Task.Run(PluginCatalog.EnsureLoaded, ct).ConfigureAwait(false);
        var used = CollectUsage(map);
        var entries = new Dictionary<string, CreditEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var ((kind, asset, rels), uses) in used)
        {
            ct.ThrowIfCancellationRequested();
            CreditEntry e;
            if (kind is "Entity" or "Trigger")
                e = ForPlugin(kind, asset, mods, ownFull);
            else
                e = ForFile(kind, asset, rels, ownFull, manifest, mods, driveByName);
            string key = kind + "|" + asset;
            if (entries.TryGetValue(key, out var have)) have.Uses += uses;
            else { e.Uses = uses; entries[key] = e; }
        }

        string? modName = ownFull == null ? null : GameAssets.EverestName(ownFull) ?? Path.GetFileName(ownFull);
        var list = entries.Values.OrderBy(e => e.Origin).ThenBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(e => e.Kind).ThenBy(e => e.Asset, StringComparer.OrdinalIgnoreCase).ToList();
        return new ScanResult(list, ownDir, modName, gotOnline);
    }

    private static string StripPng(string s) => s.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;

    /// <summary>(kind, asset name, candidate mod-relative files) → number of uses.</summary>
    private static Dictionary<(string Kind, string Asset, string[] Rels), int> CollectUsage(MapBin map)
    {
        var counts = new Dictionary<string, (string Kind, string Asset, string[] Rels, int N)>(StringComparer.OrdinalIgnoreCase);
        void Count(string kind, string asset, params string[] rels)
        {
            string k = kind + "|" + asset;
            counts[k] = counts.TryGetValue(k, out var c) ? c with { N = c.N + 1 } : (kind, asset, rels, 1);
        }

        foreach (var level in MapModel.Levels(map).Children)
        {
            foreach (string list in new[] { "fgdecals", "bgdecals" })
                foreach (var d in level.Child(list)?.Children ?? new())
                {
                    string tex = StripPng(d.Str("texture").Replace('\\', '/').Trim('/'));
                    if (tex.Length == 0) continue;
                    string rel = Atlas + "decals/" + tex;
                    Count("Decal", tex, rel + ".png", rel + "00.png", rel + "0.png", rel + "000.png");
                }
            foreach (var en in level.Child("entities")?.Children ?? new()) Count("Entity", en.Name);
            foreach (var tr in level.Child("triggers")?.Children ?? new()) Count("Trigger", tr.Name);
        }

        void Styles(BinElement? e)
        {
            if (e == null) return;
            foreach (var c in e.Children)
            {
                string tex = c.Str("texture").Replace('\\', '/').Trim('/');
                if (c.Name == "parallax" && tex.Length > 0)
                {
                    tex = StripPng(tex);
                    Count("Styleground", tex, Atlas + tex + ".png", "Graphics/Atlases/Misc/" + tex + ".png");
                }
                Styles(c);
            }
        }
        Styles(map.Root.Child("Style"));

        foreach (bool fg in new[] { true, false })
        {
            string? xml = MapModel.TilesetXml(map, fg);
            if (xml == null) continue;
            string xrel = xml.Replace('\\', '/').TrimStart('/');
            if (!xrel.StartsWith("Graphics/", StringComparison.OrdinalIgnoreCase)) xrel = "Graphics/" + xrel;
            byte[]? bytes = GameAssets.ReadModFile(xrel);
            if (bytes == null) continue;
            foreach (Match m in Regex.Matches(Encoding.UTF8.GetString(bytes), @"\bpath\s*=\s*""([^""]+)"""))
            {
                string p = m.Groups[1].Value.Replace('\\', '/').Trim('/');
                Count("Tileset", "tilesets/" + p, Atlas + "tilesets/" + p + ".png");
            }
        }

        return counts.Values.ToDictionary(c => (c.Kind, c.Asset, c.Rels), c => c.N);
    }

    private static CreditEntry ForPlugin(string kind, string name, IReadOnlyDictionary<string, BananaMod> mods, string? ownFull)
    {
        if (name.StartsWith("everest/", StringComparison.OrdinalIgnoreCase))
            return new CreditEntry { Kind = kind, Asset = name, Origin = CreditOrigin.Vanilla, CreditTo = "Everest team", Source = "Everest", Url = "https://everestapi.github.io" };
        if (ownFull != null && name.Contains('/'))
        {
            string prefix = name[..name.IndexOf('/')];
            if (prefix.Equals(Path.GetFileName(ownFull), StringComparison.OrdinalIgnoreCase)
                || prefix.Equals(GameAssets.EverestName(ownFull), StringComparison.OrdinalIgnoreCase))
                return new CreditEntry { Kind = kind, Asset = name, Origin = CreditOrigin.Own, Source = "Your mod", Note = "Your mod's own code entity." };
        }
        var def = kind == "Trigger" ? PluginCatalog.Trigger(name) : PluginCatalog.Entity(name);
        if (def == null || def.Source == "Celeste" && name.Contains('/'))
            return new CreditEntry
            {
                Kind = kind, Asset = name, Origin = name.Contains('/') ? CreditOrigin.Missing : CreditOrigin.Vanilla,
                CreditTo = name.Contains('/') ? "" : VanillaCredit, Source = name.Contains('/') ? name.Split('/')[0] + " (not installed?)" : "Celeste",
                Note = name.Contains('/') ? "Entity from a mod that isn't installed or has no Lönn plugin: find which helper it comes from and credit it." : "",
            };
        if (def.Source == "Celeste")
            return new CreditEntry { Kind = kind, Asset = name, Origin = CreditOrigin.Vanilla, CreditTo = VanillaCredit, Source = "Celeste" };
        // "SomeHelper/Thing" belongs to SomeHelper even when a plugin pack (e.g. MoreLoennPlugins) defines it for Lönn
        string? src = (name.Contains('/') ? GameAssets.SourceByName(name[..name.IndexOf('/')]) : null) ?? GameAssets.SourceByName(def.Source);
        if (src != null && ownFull != null && Path.GetFullPath(src).TrimEnd('\\', '/').Equals(ownFull, StringComparison.OrdinalIgnoreCase))
            return new CreditEntry { Kind = kind, Asset = name, Origin = CreditOrigin.Own, Source = "Your mod", Note = "Defined by your own mod's plugin." };
        return FromMod(kind, name, src, def.Source, mods);
    }

    private static CreditEntry FromMod(string kind, string asset, string? src, string fallbackName, IReadOnlyDictionary<string, BananaMod> mods)
    {
        string everest = (src == null ? null : GameAssets.EverestName(src)) ?? fallbackName;
        if (mods.TryGetValue(everest, out var b))
            return new CreditEntry
            {
                Kind = kind, Asset = asset, Origin = CreditOrigin.Mod, Source = b.Name, CreditTo = b.Author, Url = b.PageUrl,
                Note = b.Category.Length > 0 ? "GameBanana: " + b.Category : "",
            };
        return new CreditEntry
        {
            Kind = kind, Asset = asset, Origin = CreditOrigin.Mod, Source = everest,
            Note = mods.Count == 0 ? "Mod database not downloaded yet (offline?): refresh to look up the author."
                                   : "Not on GameBanana: credit the mod's author as given in its readme.",
        };
    }

    private static CreditEntry ForFile(string kind, string asset, string[] rels, string? ownFull, CreditManifest manifest,
        IReadOnlyDictionary<string, BananaMod> mods, Dictionary<string, DriveAsset> drive)
    {
        foreach (string rel in rels)
        {
            string? src = GameAssets.SourceOf(rel);
            if (src == null) continue;
            bool own = ownFull != null && Path.GetFullPath(src).TrimEnd('\\', '/').Equals(ownFull, StringComparison.OrdinalIgnoreCase);
            if (!own) return FromMod(kind, asset, src, Path.GetFileNameWithoutExtension(src), mods);

            if (manifest.Find(rel) is { } rc)
                return new CreditEntry
                {
                    Kind = kind, Asset = asset, File = rel,
                    Origin = rc.Own ? CreditOrigin.Own : CreditOrigin.Borrowed,
                    CreditTo = rc.Own ? "You" : rc.Author, Source = rc.Own ? "Your mod" : rc.Source, Url = rc.Url, Note = rc.Notes,
                };
            if (MatchDrive(asset, drive) is { } d)
                return new CreditEntry
                {
                    Kind = kind, Asset = asset, File = rel, Origin = CreditOrigin.PossiblyBorrowed,
                    CreditTo = d.CleanAuthor, Source = "Celeste Community Asset Drive", Url = MaddieDb.AssetDriveUrl,
                    Note = $"Same name as \"{d.Name}\" by {d.CleanAuthor} on the Asset Drive. Confirm and credit them, or mark it as your own work.",
                };
            return new CreditEntry
            {
                Kind = kind, Asset = asset, File = rel, Origin = CreditOrigin.Unrecorded, Source = "Your mod",
                Note = "No credit recorded. Mark it as your own work, or record who made it.",
            };
        }
        bool vanilla = rels.Any(r => GameAssets.VanillaFile(r) != null)
            || rels[0].StartsWith(Atlas, StringComparison.OrdinalIgnoreCase)
               && GameAssets.Gameplay.GetFirstFrame(StripPng(rels[0][Atlas.Length..])) != null;
        return vanilla
            ? new CreditEntry { Kind = kind, Asset = asset, Origin = CreditOrigin.Vanilla, CreditTo = VanillaCredit, Source = "Celeste" }
            : new CreditEntry { Kind = kind, Asset = asset, Origin = CreditOrigin.Missing, Note = "Texture not found in Celeste or any installed mod." };
    }

    /// <summary>An Asset Drive file whose path ends the same way as the texture (e.g. "mymod/2018HalloweenCollab/Blood_bg_a").</summary>
    public static DriveAsset? MatchDrive(string asset, Dictionary<string, DriveAsset> drive)
    {
        if (drive.Count == 0) return null;
        string a = asset.Replace('\\', '/');
        // try the longest tails first: "x/y/z", then "y/z"; a bare file name alone is too ambiguous
        var parts = a.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if (drive.TryGetValue(string.Join('/', parts[i..]), out var d)) return d;
        string baseName = a.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (baseName != a)
        {
            var bp = baseName.Split('/');
            for (int i = 0; i < bp.Length - 1; i++)
                if (drive.TryGetValue(string.Join('/', bp[i..]) + "00", out var d)) return d;
        }
        return null;
    }

    // ---- credits text ----

    /// <summary>A credits.txt for the map's mod, grouped by who to thank.</summary>
    public static string CreditsText(ScanResult r, string mapName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Credits — {mapName}" + (r.ModName != null ? $" ({r.ModName})" : ""));
        sb.AppendLine(new string('=', 60));
        sb.AppendLine();

        void Group(string title, IEnumerable<CreditEntry> items, bool byAuthor)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            sb.AppendLine(title);
            foreach (var g in list.GroupBy(e => byAuthor ? e.CreditTo + "|" + e.Source : e.Source, StringComparer.OrdinalIgnoreCase))
            {
                var f = g.First();
                string who = f.CreditTo.Length > 0 ? f.CreditTo : "unknown author";
                string what = string.Join(", ", g.GroupBy(e => e.Kind).Select(k => $"{k.Count()} {k.Key.ToLowerInvariant()}{(k.Count() == 1 ? "" : "s")}"));
                sb.Append(byAuthor ? $"  - {who}" : $"  - {f.Source} by {who}");
                if (byAuthor && f.Source.Length > 0 && f.Source != "Your mod") sb.Append($" ({f.Source})");
                sb.Append($" — {what}");
                if (f.Url.Length > 0) sb.Append($"  {f.Url}");
                sb.AppendLine();
                if (byAuthor)
                    foreach (var e in g.Take(12)) sb.AppendLine($"      {e.Kind}: {e.Asset}");
                if (byAuthor && g.Count() > 12) sb.AppendLine($"      … and {g.Count() - 12} more");
            }
            sb.AppendLine();
        }

        Group("Mods and helpers used:", r.Entries.Where(e => e.Origin == CreditOrigin.Mod), false);
        Group("Assets by other creators:", r.Entries.Where(e => e.Origin == CreditOrigin.Borrowed), true);
        var own = r.Entries.Where(e => e.Origin == CreditOrigin.Own).ToList();
        if (own.Count > 0) { sb.AppendLine($"Original assets: {own.Count} made for this map."); sb.AppendLine(); }
        if (r.Entries.Any(e => e.Origin == CreditOrigin.Vanilla))
        {
            sb.AppendLine($"Celeste assets by {VanillaCredit}.");
            sb.AppendLine();
        }
        var todo = r.Entries.Where(e => e.NeedsAttention).ToList();
        if (todo.Count > 0)
        {
            sb.AppendLine("TODO before release — credit not confirmed:");
            foreach (var e in todo)
                sb.AppendLine($"  - {e.Kind}: {e.Asset}" + (e.CreditTo.Length > 0 ? $" (maybe {e.CreditTo})" : "") + (e.Note.Length > 0 ? $" — {e.Note}" : ""));
            sb.AppendLine();
        }
        sb.AppendLine("Credit lookup by MT_Zenith using Maddie480's Celeste services (https://maddie480.ovh):");
        sb.AppendLine("GameBanana mod database and the Celeste Community Asset Drive.");
        return sb.ToString();
    }

    // ---- bringing assets into the mod ----

    /// <summary>Where an imported file goes in the mod for an Asset Drive category.</summary>
    public static string ImportFolder(string category, string modName) => category switch
    {
        "decals" => $"{Atlas}decals/{modName}/",
        "stylegrounds" => $"{Atlas}bgs/{modName}/",
        "fgtilesets" or "bgtilesets" => $"{Atlas}tilesets/{modName}/",
        "hires" => $"Graphics/Atlases/Gui/{modName}/",
        _ => $"{Atlas}{modName}/",
    };

    public static string SafeSegment(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var t = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return t.Length == 0 ? "asset" : t;
    }

    /// <summary>Writes a file into the mod and records its credit. Returns the mod-relative path.</summary>
    public static string Import(string modDir, string rel, byte[] data, RecordedCredit credit)
    {
        rel = string.Join('/', rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeSegment));
        string full = Path.GetFullPath(Path.Combine(modDir, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(modDir), StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escapes the mod folder.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        var m = CreditManifest.Load(modDir);
        credit.Path = rel;
        m.Set(credit);
        m.Save(modDir);
        return rel;
    }

    /// <summary>Atlas texture name (decal "texture" value) for a mod-relative decal file.</summary>
    public static string? DecalNameOf(string rel)
    {
        const string p = Atlas + "decals/";
        return rel.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? StripPng(rel[p.Length..]) : null;
    }
}

/// <summary>Recommendations: what to do about each credit entry, and Asset Drive files that suit the open map.</summary>
public static class CreditAdvice
{
    /// <summary>The recommended next step for one entry.</summary>
    public static string NextStep(CreditEntry e) => e.Origin switch
    {
        CreditOrigin.Mod when e.Url.Length > 0 => $"Credit \"{e.Source}\" by {e.CreditTo} and list it as a dependency.",
        CreditOrigin.Mod => $"Credit {e.Source} (check its readme for the author) and list it as a dependency.",
        CreditOrigin.Borrowed => $"Keep crediting {e.CreditTo}; follow their terms.",
        CreditOrigin.Own => "Nothing to do: your own work.",
        CreditOrigin.PossiblyBorrowed => $"Likely by {e.CreditTo}: open it on the Asset Drive, compare, then Confirm match.",
        CreditOrigin.Unrecorded => "Mark as your own work, or Credit someone… if you took it from elsewhere.",
        CreditOrigin.Vanilla => "Nothing to do: Celeste/Everest content.",
        _ => e.Kind is "Entity" or "Trigger"
            ? "Install the helper (add it to everest.yaml dependencies) or remove the entity."
            : "Fix the texture path, or add the file to your mod.",
    };

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "the", "and", "for", "bg", "fg", "decals", "decal", "tilesets", "tileset", "bgs", "misc", "give", "credit",
        "notify", "room", "lvl", "new", "copy", "alt", "base", "set", "pack", "small", "big", "large", "mid", "top", "bot", "bottom",
        "left", "right", "end", "start", "rot", "recolor", "recolors", "vanilla", "asset", "assets", "frame", "idle", "var", "variant",
    };

    /// <summary>Lower-case words in a path or name: split on separators, digits and camelCase.</summary>
    public static IEnumerable<string> Words(string s)
    {
        foreach (Match m in Regex.Matches(s, @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])"))
        {
            string w = m.Value.ToLowerInvariant();
            if (w.Length >= 3 && !Stop.Contains(w)) yield return w;
        }
    }

    /// <summary>Theme words of a map (from its decals, stylegrounds, tilesets and room names) with weights.</summary>
    public static Dictionary<string, double> MapKeywords(MapBin map)
    {
        var w = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Add(string s, double weight) { foreach (string x in Words(s)) w[x] = w.GetValueOrDefault(x) + weight; }
        foreach (var level in MapModel.Levels(map).Children)
        {
            Add(level.Str("name"), 0.5);
            foreach (string list in new[] { "fgdecals", "bgdecals" })
                foreach (var d in level.Child(list)?.Children ?? new()) Add(d.Str("texture"), 1);
        }
        void Styles(BinElement? e) { if (e == null) return; foreach (var c in e.Children) { Add(c.Str("texture"), 3); Styles(c); } }
        Styles(map.Root.Child("Style"));
        // Diminishing returns so one decal used 500 times doesn't drown everything else.
        return w.ToDictionary(k => k.Key, k => Math.Log(1 + k.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asset Drive files ranked for a map: words shared with the map's own decals/stylegrounds/rooms, plus a bonus for
    /// creators the map already credits (consistent art style, one more name in the same credits line).
    /// </summary>
    public static List<(DriveAsset Asset, double Score, string Why)> Recommend(IEnumerable<DriveAsset> drive,
        Dictionary<string, double> keywords, ISet<string> creditedAuthors, int max = 300)
    {
        var scored = new List<(DriveAsset, double, string)>();
        foreach (var a in drive)
        {
            var hits = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (string word in Words(a.Name + " " + a.Folder + " " + string.Join(' ', a.Tags ?? new())))
                if (keywords.TryGetValue(word, out double k)) hits[word] = k;
            double score = hits.Values.Sum();
            bool sameAuthor = creditedAuthors.Contains(a.CleanAuthor);
            if (sameAuthor) score += 2;
            if (score <= 0) continue;
            var why = new List<string>();
            if (hits.Count > 0) why.Add("matches your map's " + string.Join(", ", hits.OrderByDescending(h => h.Value).Take(4).Select(h => h.Key)));
            if (sameAuthor) why.Add($"by {a.CleanAuthor}, who you already credit");
            scored.Add((a, score, string.Join("; ", why)));
        }
        // Variety: at most 3 picks from one folder, so one big pack doesn't fill the list.
        var perFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(DriveAsset, double, string)>();
        foreach (var x in scored.OrderByDescending(s => s.Item2))
        {
            string folder = x.Item1.Folder + "/" + Path.GetDirectoryName(x.Item1.Name);
            int n = perFolder.GetValueOrDefault(folder);
            if (n >= 3) continue;
            perFolder[folder] = n + 1;
            result.Add(x);
            if (result.Count >= max) break;
        }
        return result;
    }
}
