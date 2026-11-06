using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CELMapViewer.Agent;

/// <summary>A map from an installed mod (loose or zipped) that agents may study for design reference.</summary>
public sealed record RefMap(string Source, string Rel, string Name, int? Tier, string Kind, string Stamp, Func<byte[]?> Read)
{
    public string Key => Source + ":" + Rel;
}

/// <summary>Design statistics of one map; per-screen densities use 320x180 screens.</summary>
public sealed class MapStats
{
    public string Stamp { get; set; } = "";
    public Dictionary<string, double> Metrics { get; set; } = new();
    public Dictionary<string, int> Entities { get; set; } = new();
}

/// <summary>
/// Finds installed maps (Strawberry Jam, Spring Collab, CEL, ...) and derives difficulty-tier statistics from collab
/// folder names like "1-Beginner" .. "5-Grandmaster", so agents can design levels at a given difficulty.
/// </summary>
public static class ReferenceMaps
{
    public static readonly string[] TierNames = { "", "Beginner", "Intermediate", "Advanced", "Expert", "Grandmaster" };

    private static readonly Regex TierSegment = new(@"^\d+-(beginner|intermediate|advanced|expert|grandmaster)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const double ScreenArea = 320.0 * 180.0;
    private const string StatsVersion = "2|"; // bump when Analyze/Categories change to invalidate cached stats

    /// <summary>Metric keys in report order, with a short description.</summary>
    public static readonly (string Key, string Desc)[] MetricInfo =
    {
        ("rooms", "rooms in the map"),
        ("screens", "map area in 320x180 screens"),
        ("screens_per_room", "average room size in screens"),
        ("max_room_screens", "largest room in screens"),
        ("solid_ratio", "share of fg cells that are solid"),
        ("spawns_per_screen", "player spawns (checkpoint frequency)"),
        ("entities_per_screen", "all entities"),
        ("triggers_per_screen", "all triggers"),
        ("distinct_entity_types", "different entity types used"),
        ("spikes_per_screen", "spikes"),
        ("spinners_per_screen", "crystal/dust spinners, blades"),
        ("other_hazards_per_screen", "lightning, seekers, fireballs, lava, lasers, ..."),
        ("refills_per_screen", "dash refills"),
        ("springs_per_screen", "springs"),
        ("boosters_per_screen", "boosters/bubbles"),
        ("feathers_per_screen", "feathers"),
        ("moving_blocks_per_screen", "zip movers, move/swap/crush blocks, moving platforms"),
        ("falling_blocks_per_screen", "crumble and falling blocks"),
        ("dream_blocks_per_screen", "dream blocks"),
        ("dash_blocks_per_screen", "dash blocks/switches, bounce blocks"),
        ("jumpthrus_per_screen", "jump-through platforms"),
        ("collectibles_per_screen", "strawberries, hearts, cassettes"),
    };

    // First match wins; matched against the lowercased type name after the last '/', so "SpringCollab2020/x" isn't a spring.
    private static readonly (string Cat, string[] Words)[] Categories =
    {
        ("spikes", new[] { "spike" }),
        ("collectibles", new[] { "strawberry", "berry", "heartgem", "cassette" }),
        ("dream_blocks", new[] { "dreamblock" }),
        ("falling_blocks", new[] { "crumble", "fallingblock", "fallblock" }),
        ("spinners", new[] { "spinner", "dustbunny", "dustgraphic", "bladetrack", "starrotate", "blade" }),
        ("other_hazards", new[] { "lightning", "seeker", "fireball", "firebar", "firebarrier", "icebarrier", "lava", "laser", "killbox", "snowball", "oshiro", "deadly" }),
        ("refills", new[] { "refill" }),
        ("springs", new[] { "spring" }),
        ("boosters", new[] { "booster", "bubble" }),
        ("feathers", new[] { "feather" }),
        ("moving_blocks", new[] { "zipmover", "zip", "moveblock", "movingblock", "movingplatform", "swapblock", "swap", "crushblock", "kevin", "sinkingplatform", "tracked" }),
        ("dash_blocks", new[] { "dashblock", "dashswitch", "bounceblock", "templecracked" }),
        ("jumpthrus", new[] { "jumpthru", "jumpthrough" }),
    };

    public static string? Category(string entityName)
    {
        int slash = entityName.LastIndexOf('/');
        string s = (slash >= 0 ? entityName[(slash + 1)..] : entityName).ToLowerInvariant();
        if (s.Contains("controller")) return null;
        foreach (var (cat, words) in Categories)
            foreach (string w in words)
                if (s.Contains(w)) return cat;
        return null;
    }

    private static readonly string[] Guidance =
    {
        "",
        "Beginner: teach one mechanic at a time — show it safely first, then ask for it with generous spacing. " +
        "Wide rooms, forgiving gaps, plenty of refills and frequent checkpoints (about one room per screen). " +
        "No required advanced tech (hypers, wavedashes, ultras); hazards frame the path rather than threaten precision. " +
        "Short chapters with an optional berry or two that ask a bit more.",
        "Intermediate: combine two familiar mechanics per room, tighten spacing and timing, and let momentum matter. " +
        "Basic tech (corner kicks, dash-jumps, simple hypers/supers) may help but should rarely be mandatory. " +
        "Rooms grow to 1–2 screens; hazards start to constrain routes.",
        "Advanced: rooms expect hypers, wavedashes, ultras and wallbounces, plus mechanic interactions (e.g. a booster into a zip mover). " +
        "Denser spinners/spikes, fewer refills, longer rooms between checkpoints, tighter windows.",
        "Expert: long multi-part rooms with demanding tech chains (demodashes, cornerboosts, reverse hypers, bubsdrops), " +
        "speed preservation and hazards placed tight to the intended line; refills are scarce and deliberate.",
        "Grandmaster: frame-tight execution, extended tech combinations and very long rooms with minimal checkpoints. " +
        "Assumes mastery of every common tech and fast decision making; difficulty comes from precision and consistency, not obscurity. " +
        "Still readable: every room should have a clear intended route.",
    };

    public static string GuidanceFor(int tier) => tier is >= 1 and <= 5 ? Guidance[tier] : "";

    /// <summary>Tier from a name ("expert"), a folder ("4-Expert") or a number (1-5).</summary>
    public static int ParseTier(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) throw new ToolException("Missing tier (beginner, intermediate, advanced, expert, grandmaster or 1-5).");
        s = s.Trim();
        if (int.TryParse(s, out int n) && n is >= 1 and <= 5) return n;
        string t = s.Contains('-') ? s[(s.IndexOf('-') + 1)..] : s;
        for (int i = 1; i < TierNames.Length; i++)
            if (TierNames[i].StartsWith(t, StringComparison.OrdinalIgnoreCase) && t.Length >= 3) return i;
        if (t.Equals("gm", StringComparison.OrdinalIgnoreCase)) return 5;
        throw new ToolException($"Unknown tier '{s}'. Use beginner, intermediate, advanced, expert, grandmaster or 1-5.");
    }

    // ---- discovery ----

    private static readonly object Gate = new();
    private static List<RefMap>? _maps;

    public static IReadOnlyList<RefMap> All(bool refresh = false)
    {
        lock (Gate)
        {
            if (_maps != null && !refresh) return _maps;
            var list = new List<RefMap>();
            foreach (var (src, rel, stamp, read) in GameAssets.EnumerateModFilesStamped("Maps", ".bin"))
                list.Add(Describe(src, rel, stamp, read));
            list.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            return _maps = list;
        }
    }

    private static RefMap Describe(string src, string rel, string stamp, Func<byte[]?> read)
    {
        string[] seg = rel.Split('/');
        string file = Path.GetFileNameWithoutExtension(rel);
        int? tier = null;
        foreach (string s in seg)
        {
            var m = TierSegment.Match(s);
            if (m.Success) tier = Array.FindIndex(TierNames, t => t.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        }
        string lower = rel.ToLowerInvariant();
        string kind = lower.Contains("lobb") ? "lobby"
            : lower.Contains("gym") ? "gym"
            : file.StartsWith("ZZ-", StringComparison.OrdinalIgnoreCase) || lower.Contains("heartside") ? "heartside"
            : lower.Contains("/wip/") ? "wip"
            : "map";
        return new RefMap(src, rel, file, tier, kind, stamp, read);
    }

    /// <summary>Resolves a map by key ("Source:Maps/..."), path, path suffix or file name.</summary>
    public static RefMap Find(string query)
    {
        string q = query.Replace('\\', '/').Trim();
        var all = All();
        RefMap[] Match(Func<RefMap, bool> f) => all.Where(f).ToArray();
        string qb = q.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ? q : q + ".bin";
        foreach (var cand in new[]
        {
            Match(m => m.Key.Equals(q, StringComparison.OrdinalIgnoreCase)),
            Match(m => m.Rel.Equals(qb, StringComparison.OrdinalIgnoreCase) || m.Rel.Equals("Maps/" + qb, StringComparison.OrdinalIgnoreCase)),
            Match(m => m.Rel.EndsWith("/" + qb, StringComparison.OrdinalIgnoreCase)),
            Match(m => m.Name.Equals(q, StringComparison.OrdinalIgnoreCase)),
            Match(m => m.Rel.Contains(q, StringComparison.OrdinalIgnoreCase)),
        })
        {
            if (cand.Length == 1) return cand[0];
            if (cand.Length > 1)
                throw new ToolException($"'{query}' matches {cand.Length} maps; be more specific: " +
                    string.Join(", ", cand.Take(12).Select(m => m.Key)) + (cand.Length > 12 ? ", ..." : ""));
        }
        throw new ToolException($"No reference map matches '{query}'. Use list_reference_maps.");
    }

    // ---- loading and stats ----

    private static (string Key, MapBin Map)? _lastLoaded;

    public static MapBin Load(RefMap m)
    {
        lock (Gate)
            if (_lastLoaded is { } l && l.Key == m.Key + "|" + m.Stamp) return l.Map;
        byte[] bytes = m.Read() ?? throw new ToolException($"Can't read {m.Key}.");
        using var ms = new MemoryStream(bytes);
        var map = MapBin.Load(ms, m.Name);
        lock (Gate) _lastLoaded = (m.Key + "|" + m.Stamp, map);
        return map;
    }

    /// <summary>Statistics of a map. Read-only: never adds elements to the map.</summary>
    public static MapStats Analyze(MapBin map)
    {
        var st = new MapStats();
        var cats = Categories.ToDictionary(c => c.Cat, _ => 0);
        double area = 0, maxRoom = 0;
        long cells = 0, solid = 0;
        int rooms = 0, spawns = 0, entities = 0, triggers = 0;
        foreach (var el in MapModel.Levels(map).Children)
        {
            var r = new Room(el);
            rooms++;
            double a = (double)r.W * r.H;
            area += a;
            maxRoom = Math.Max(maxRoom, a / ScreenArea);
            var g = r.Grid(true);
            for (int y = 0; y < g.Rows; y++)
                for (int x = 0; x < g.Cols; x++)
                    if (g[x, y] != '0') solid++;
            cells += (long)g.Cols * g.Rows;
            foreach (var e in el.Child("entities")?.Children ?? new())
            {
                if (MapModel.IsSpawn(e)) { spawns++; continue; }
                entities++;
                st.Entities[e.Name] = st.Entities.GetValueOrDefault(e.Name) + 1;
                if (Category(e.Name) is { } c) cats[c]++;
            }
            triggers += el.Child("triggers")?.Children.Count ?? 0;
        }
        double screens = Math.Max(area / ScreenArea, 0.01);
        var mt = st.Metrics;
        mt["rooms"] = rooms;
        mt["screens"] = Math.Round(area / ScreenArea, 2);
        mt["screens_per_room"] = rooms > 0 ? Math.Round(area / ScreenArea / rooms, 2) : 0;
        mt["max_room_screens"] = Math.Round(maxRoom, 2);
        mt["solid_ratio"] = cells > 0 ? Math.Round((double)solid / cells, 3) : 0;
        mt["spawns_per_screen"] = Math.Round(spawns / screens, 3);
        mt["entities_per_screen"] = Math.Round(entities / screens, 3);
        mt["triggers_per_screen"] = Math.Round(triggers / screens, 3);
        mt["distinct_entity_types"] = st.Entities.Count;
        foreach (var (c, n) in cats) mt[c + "_per_screen"] = Math.Round(n / screens, 3);
        return st;
    }

    // ---- stats cache (memory + %APPDATA%\MT_Zenith\refprofiles.json) ----

    private static ConcurrentDictionary<string, MapStats>? _cache;
    private static string CachePath => Path.Combine(AppInfo.DataDir, "refprofiles.json");

    private static ConcurrentDictionary<string, MapStats> Cache
    {
        get
        {
            lock (Gate)
            {
                if (_cache != null) return _cache;
                try
                {
                    if (File.Exists(CachePath) && JsonSerializer.Deserialize<Dictionary<string, MapStats>>(File.ReadAllText(CachePath)) is { } d)
                        return _cache = new ConcurrentDictionary<string, MapStats>(d);
                }
                catch { /* corrupt cache: rebuild */ }
                return _cache = new ConcurrentDictionary<string, MapStats>();
            }
        }
    }

    private static void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            string tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new SortedDictionary<string, MapStats>(Cache)));
            File.Move(tmp, CachePath, true);
        }
        catch { /* cache is optional */ }
    }

    public static MapStats StatsOf(RefMap m) => StatsOf(new[] { m })[0];

    /// <summary>Stats for several maps, analysing uncached ones in parallel; null entries for unreadable maps.</summary>
    public static MapStats[] StatsOf(IReadOnlyList<RefMap> maps)
    {
        var result = new MapStats[maps.Count];
        var cache = Cache;
        int added = 0;
        Parallel.For(0, maps.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) }, i =>
        {
            var m = maps[i];
            if (cache.TryGetValue(m.Key, out var s) && s.Stamp == StatsVersion + m.Stamp) { result[i] = s; return; }
            try
            {
                byte[]? bytes = m.Read();
                if (bytes == null) return;
                using var ms = new MemoryStream(bytes);
                s = Analyze(MapBin.Load(ms, m.Name));
                s.Stamp = StatsVersion + m.Stamp;
                cache[m.Key] = s;
                result[i] = s;
                Interlocked.Increment(ref added);
            }
            catch { /* unreadable map: skip */ }
        });
        if (added > 0) SaveCache();
        return result;
    }

    // ---- tier profiles ----

    public sealed record MetricRange(double P25, double Median, double P75);

    public sealed record TierProfile(int Tier, int MapCount, Dictionary<string, MetricRange> Metrics,
        List<(string Type, double MapShare, double PerScreen)> Common, List<RefMap> Examples);

    private static double Quantile(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        double pos = (sorted.Count - 1) * q;
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return Math.Round(sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo), 3);
    }

    public static TierProfile Profile(int tier)
    {
        var maps = All().Where(m => m.Tier == tier && m.Kind == "map").ToList();
        if (maps.Count == 0)
            throw new ToolException($"No installed maps are tagged {TierNames[tier]} (collab folders like '{tier}-{TierNames[tier]}'). " +
                                    "Install Strawberry Jam or Spring Collab, or study specific maps with study_reference_map.");
        var stats = StatsOf(maps);
        var ok = Enumerable.Range(0, maps.Count).Where(i => stats[i] != null).ToList();
        var metrics = new Dictionary<string, MetricRange>();
        foreach (var (key, _) in MetricInfo)
        {
            var v = ok.Select(i => stats[i].Metrics.GetValueOrDefault(key)).OrderBy(x => x).ToList();
            metrics[key] = new MetricRange(Quantile(v, 0.25), Quantile(v, 0.5), Quantile(v, 0.75));
        }
        var types = new Dictionary<string, (int Maps, List<double> Density)>();
        foreach (int i in ok)
        {
            double screens = Math.Max(stats[i].Metrics.GetValueOrDefault("screens"), 0.01);
            foreach (var (t, n) in stats[i].Entities)
            {
                if (!types.TryGetValue(t, out var e)) types[t] = e = (0, new List<double>());
                types[t] = (e.Maps + 1, e.Density);
                e.Density.Add(n / screens);
            }
        }
        var common = types.OrderByDescending(kv => kv.Value.Maps).ThenBy(kv => kv.Key).Take(25)
            .Select(kv => (kv.Key, Math.Round((double)kv.Value.Maps / ok.Count, 2), Quantile(kv.Value.Density.OrderBy(x => x).ToList(), 0.5)))
            .ToList();
        // Examples: maps closest to the tier's median size, a good starting point for study.
        double med = metrics["screens"].Median;
        var examples = ok.OrderBy(i => Math.Abs(stats[i].Metrics.GetValueOrDefault("screens") - med)).Take(5).Select(i => maps[i]).ToList();
        return new TierProfile(tier, ok.Count, metrics, common, examples);
    }

    public static JsonObject ProfileJson(TierProfile p)
    {
        var metrics = new JsonObject();
        foreach (var (key, desc) in MetricInfo)
        {
            var r = p.Metrics[key];
            metrics[key] = new JsonObject { ["p25"] = r.P25, ["median"] = r.Median, ["p75"] = r.P75, ["about"] = desc };
        }
        return new JsonObject
        {
            ["tier"] = p.Tier,
            ["name"] = TierNames[p.Tier],
            ["maps_analyzed"] = p.MapCount,
            ["guidance"] = GuidanceFor(p.Tier),
            ["metrics"] = metrics,
            ["common_entities"] = new JsonArray(p.Common.Select(c => (JsonNode)new JsonObject
            {
                ["type"] = c.Type, ["share_of_maps"] = c.MapShare, ["median_per_screen_when_used"] = c.PerScreen
            }).ToArray()),
            ["example_maps"] = new JsonArray(p.Examples.Select(m => (JsonNode)m.Key).ToArray()),
        };
    }

    public static JsonObject StatsJson(MapStats s, int topTypes = 20)
    {
        var metrics = new JsonObject();
        foreach (var (key, _) in MetricInfo) metrics[key] = s.Metrics.GetValueOrDefault(key);
        return new JsonObject
        {
            ["metrics"] = metrics,
            ["top_entities"] = new JsonObject(s.Entities.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(topTypes)
                .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        };
    }

    /// <summary>Per-metric comparison of a map's stats with a tier profile.</summary>
    public static JsonObject Compare(MapStats s, TierProfile p)
    {
        var rows = new JsonObject();
        int low = 0, high = 0;
        foreach (var (key, desc) in MetricInfo)
        {
            double v = s.Metrics.GetValueOrDefault(key);
            var r = p.Metrics[key];
            string verdict = v < r.P25 ? "lower" : v > r.P75 ? "higher" : "typical";
            if (verdict == "lower") low++; else if (verdict == "higher") high++;
            rows[key] = new JsonObject { ["value"] = v, ["p25"] = r.P25, ["median"] = r.Median, ["p75"] = r.P75, ["verdict"] = verdict, ["about"] = desc };
        }
        return new JsonObject
        {
            ["tier"] = TierNames[p.Tier],
            ["maps_in_profile"] = p.MapCount,
            ["summary"] = $"{MetricInfo.Length - low - high} metrics typical for {TierNames[p.Tier]}, {low} lower, {high} higher. " +
                          "Numbers describe density and size only; difficulty also depends on spacing and required tech, so playtest.",
            ["metrics"] = rows,
            ["guidance"] = GuidanceFor(p.Tier),
        };
    }
}
