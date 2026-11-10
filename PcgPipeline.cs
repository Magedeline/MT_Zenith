using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using CELMapViewer.Agent;

namespace CELMapViewer;

public sealed class PcgPipelineOptions
{
    public PcgOptions Base = new();
    /// <summary>Levels generated (seed, seed#1, seed#2, ...) and scored; the best one is kept.</summary>
    public int Candidates = 1;
    /// <summary>Fix Markov artefacts: floating spikes, buried spinners, spawns without ground, blocked exit mouths.</summary>
    public bool Repair = true;
    /// <summary>1..5: trim hazards toward that tier's density and score candidates against it.</summary>
    public int? TargetTier;
    /// <summary>Entity names kept (player and the heart are always kept); null = everything the model produces.</summary>
    public HashSet<string>? Allowed;
    public bool Analyze = true;
}

public sealed class PcgCandidate
{
    public PcgResult Result = new();
    public double Score;
    public JsonObject Report = new();
    public List<string> Fixes = new();
}

/// <summary>
/// Everything around the raw seleCte sampler: generate several candidate levels and keep the best, repair the artefacts
/// a Markov chain leaves behind, filter mechanics, trim hazards to a difficulty tier, judge every room with the movement
/// model (<see cref="RoomAnalysis"/>), render a preview and build training corpora from maps.
/// </summary>
public static class PcgPipeline
{
    /// <summary>Mechanic groups the generator's alphabet can produce, as user-facing labels over entity names.</summary>
    public static readonly (string Label, string[] Names)[] Mechanics =
    {
        ("spikes", new[] { "spikesUp", "spikesDown", "spikesLeft", "spikesRight" }),
        ("spinners", new[] { "spinner" }),
        ("bumpers", new[] { "bigSpinner" }),
        ("dream blocks", new[] { "dreamBlock" }),
        ("crumble blocks", new[] { "crumbleBlock" }),
        ("jump-throughs", new[] { "jumpThru" }),
        ("boosters", new[] { "booster" }),
        ("water", new[] { "water" }),
        ("refills", new[] { "refill" }),
        ("feathers", new[] { "infiniteStar" }),
    };

    /// <summary>Hazard cells per passable cell a tier tolerates (above it the repair pass removes hazards).</summary>
    public static readonly double[] TierHazardCap = { 0, 0.02, 0.045, 0.08, 0.12, 0.17 };
    /// <summary>Hazard pressure (lethal cells touching the reachable space) that feels like each tier.</summary>
    public static readonly double[] TierPressure = { 0, 0.04, 0.09, 0.15, 0.22, 0.30 };

    /// <summary>Entity names (or mechanic labels) → the allowed set; null when nothing was given.</summary>
    public static HashSet<string>? ParseAllowed(IEnumerable<string>? names)
    {
        if (names == null) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in names)
        {
            string n = raw.Trim();
            if (n.Length == 0) continue;
            var group = Mechanics.FirstOrDefault(m => m.Label.Equals(n, StringComparison.OrdinalIgnoreCase) || m.Label.Replace(" ", "").Replace("-", "").Equals(n.Replace(" ", "").Replace("-", ""), StringComparison.OrdinalIgnoreCase));
            if (group.Names != null) set.UnionWith(group.Names);
            else set.Add(n);
        }
        return set.Count == 0 ? null : set;
    }

    // ---- candidate search ----

    public static PcgCandidate Run(PcgGenerator gen, PcgPipelineOptions o, Action<string> log, CancellationToken ct = default)
    {
        int n = Math.Clamp(o.Candidates, 1, 24);
        string baseSeed = o.Base.Seed;
        PcgCandidate? best = null;
        var table = new List<string>();
        for (int k = 0; k < n; k++)
        {
            ct.ThrowIfCancellationRequested();
            var opts = o.Base.Clone();
            if (k > 0) opts.Seed = baseSeed + "#" + k;
            var res = gen.Generate(opts, n == 1 ? log : _ => { });
            if (k == 0 && baseSeed.Length == 0) baseSeed = res.Seed; // a random seed is reported; variants derive from it
            var cand = new PcgCandidate { Result = res };
            if (o.Repair) cand.Fixes = Repair(res, o.Allowed, o.TargetTier, res.Seed.GetHashCode());
            else if (o.Allowed != null) cand.Fixes = Filter(res, o.Allowed);
            if (o.Analyze || n > 1)
            {
                cand.Report = Analyze(res, out double playable, out double pressure, out int variety, out int unreachableExits);
                cand.Score = Score(res, playable, pressure, variety, unreachableExits, o.TargetTier);
                cand.Report["score"] = Math.Round(cand.Score, 1);
            }
            if (cand.Fixes.Count > 0) cand.Report["repairs"] = new JsonArray(cand.Fixes.Take(60).Select(f => (JsonNode)f).ToArray());
            table.Add($"  candidate {k + 1}: seed \"{res.Seed}\" score {cand.Score:0.0} ({res.Rooms.Count} rooms, {(res.AllPlayable ? "route ok" : "route NOT ok")}, {cand.Fixes.Count} repairs)");
            if (best == null || cand.Score > best.Score) best = cand;
        }
        if (n > 1)
        {
            log($"Scored {n} candidates:\n{string.Join("\n", table)}\nKeeping seed \"{best!.Result.Seed}\".");
            best.Report["candidates"] = new JsonArray(table.Select(t => (JsonNode)t.Trim()).ToArray());
        }
        return best!;
    }

    private static double Score(PcgResult res, double playable, double pressure, int variety, int unreachableExits, int? tier)
    {
        double s = 55 * playable + 12 * Math.Min(1, variety / 6.0) + (res.AllPlayable ? 8 : 0) - 4 * Math.Min(5, unreachableExits);
        if (tier is int t)
        {
            double target = TierPressure[t];
            s += 25 * Math.Max(0, 1 - Math.Abs(pressure - target) / 0.2);
        }
        else s += pressure is > 0.03 and < 0.28 ? 15 : 5;
        return Math.Max(0, s);
    }

    // ---- analysis ----

    /// <summary>A room element for a generated room at a position (mirrors MapModel.ImportPcg for one room).</summary>
    public static BinElement ToLevel(PcgRoom r, string name, int x, int y, ref int id)
    {
        string bg = r.Bg != null ? string.Join("\n", r.Bg) : string.Join("\n", Enumerable.Repeat(new string('0', r.WTiles), r.HTiles));
        var level = MapModel.NewLevel(name, x, y, r.W, r.H, string.Join("\n", r.Fg), bg);
        var entities = level.Child("entities")!;
        foreach (var e in r.Entities)
        {
            var el = new BinElement(e.Name);
            el.Set("id", id++);
            el.Set("x", e.X);
            el.Set("y", e.Y);
            if (e.W != null) el.Set("width", e.W.Value);
            if (e.H != null) el.Set("height", e.H.Value);
            foreach (var (k, v) in e.Attrs) el.Set(k, v);
            entities.Children.Add(el);
        }
        return level;
    }

    public static Room TempRoom(PcgRoom r)
    {
        int id = 1;
        return new Room(ToLevel(r, r.Name, 0, 0, ref id));
    }

    private static List<(int X, int Y)> SpawnCells(Room room) =>
        room.EntitiesEl.Children.Where(MapModel.IsSpawn)
            .Select(e => (Math.Clamp((int)Math.Floor(e.Num("x") / 8f), 0, room.Cols - 1), Math.Clamp((int)Math.Floor((e.Num("y") - 1) / 8f), 0, room.Rows - 1)))
            .ToList();

    /// <summary>Per-room movement-model judgement of a generated level (from every spawn, since seleCte adds a respawn at each entrance).</summary>
    public static JsonObject Analyze(PcgResult res, out double playableFraction, out double meanPressure, out int variety, out int unreachableExits)
    {
        var rooms = new JsonArray();
        int playable = 0, exitsTotal = 0, exitsReached = 0;
        double pressureSum = 0;
        var types = new HashSet<string>();
        unreachableExits = 0;
        foreach (var pr in res.Rooms)
        {
            var room = TempRoom(pr);
            foreach (var e in pr.Entities) if (e.Name != "player") types.Add(e.Name);
            var spawns = SpawnCells(room);
            var (basic, report) = RoomAnalysis.Report(room, spawns.Count > 0 ? spawns : null);
            int reached = basic.Exits.Count(e => e.Reached);
            exitsTotal += basic.Exits.Count;
            exitsReached += reached;
            unreachableExits += basic.Exits.Count(e => e.MinTech == null);
            bool ok = basic.Exits.Count > 0 && reached == basic.Exits.Count;
            if (ok) playable++;
            pressureSum += basic.HazardPressure;
            rooms.Add(new JsonObject
            {
                ["room"] = pr.Name, ["size"] = $"{pr.WTiles}x{pr.HTiles}", ["spawns"] = spawns.Count,
                ["exits"] = basic.Exits.Count, ["exits_reached"] = reached,
                ["required_tech"] = report["required_tech"]?.DeepClone(),
                ["hazard_pressure"] = Math.Round(basic.HazardPressure, 3),
                ["entities"] = pr.Entities.Count(e => e.Name != "player"),
                ["hints"] = new JsonArray(basic.Exits.Where(e => e.MinTech is null or Tech.Advanced).Select(e => (JsonNode)$"exit {e.Label}: {(e.MinTech == null ? "unreachable" : "advanced tech")}").ToArray()),
            });
        }
        playableFraction = res.Rooms.Count > 0 ? playable / (double)res.Rooms.Count : 0;
        meanPressure = res.Rooms.Count > 0 ? pressureSum / res.Rooms.Count : 0;
        variety = types.Count;
        return new JsonObject
        {
            ["seed"] = res.Seed, ["rooms"] = res.Rooms.Count, ["route"] = string.Join(" -> ", res.Route),
            ["selecte_route_playable"] = res.AllPlayable,
            ["rooms_fully_traversable"] = $"{playable}/{res.Rooms.Count}",
            ["exits_reachable"] = $"{exitsReached}/{exitsTotal}",
            ["mean_hazard_pressure"] = Math.Round(meanPressure, 3),
            ["tier_feel"] = TierFeel(meanPressure),
            ["entity_types"] = string.Join(", ", types.OrderBy(t => t)),
            ["room_details"] = rooms,
            ["elapsed_s"] = Math.Round(res.Elapsed.TotalSeconds, 2),
        };
    }

    public static string TierFeel(double pressure)
    {
        int best = 1;
        for (int t = 1; t <= 5; t++) if (Math.Abs(pressure - TierPressure[t]) < Math.Abs(pressure - TierPressure[best])) best = t;
        return ReferenceMaps.TierNames[best];
    }

    // ---- repair ----

    private static char[][] Cells(PcgRoom r)
    {
        var g = new char[r.HTiles][];
        for (int y = 0; y < r.HTiles; y++)
        {
            g[y] = new char[r.WTiles];
            string row = y < r.Fg.Length ? r.Fg[y] : "";
            for (int x = 0; x < r.WTiles; x++) g[y][x] = x < row.Length ? row[x] : '0';
        }
        return g;
    }

    private static void Store(PcgRoom r, char[][] g) => r.Fg = g.Select(row => new string(row)).ToArray();

    private static bool Solid(char[][] g, int x, int y) => y >= 0 && y < g.Length && x >= 0 && x < g[y].Length && g[y][x] != '0';

    private static List<string> Filter(PcgResult res, HashSet<string> allowed)
    {
        var fixes = new List<string>();
        foreach (var r in res.Rooms)
        {
            int n = r.Entities.RemoveAll(e => e.Name is not ("player" or "blackGem") && !allowed.Contains(e.Name));
            if (n > 0) fixes.Add($"{r.Name}: removed {n} entities outside the allowed mechanics");
        }
        return fixes;
    }

    /// <summary>Cleans the artefacts the Markov sampler leaves and shapes the level toward a tier. Returns a log of what changed.</summary>
    public static List<string> Repair(PcgResult res, HashSet<string>? allowed, int? tier, int seed)
    {
        var fixes = allowed != null ? Filter(res, allowed) : new List<string>();
        var rng = new Random(seed);
        foreach (var r in res.Rooms)
        {
            var g = Cells(r);
            int w = r.WTiles, h = r.HTiles;
            var kept = new List<PcgEntity>();
            int floating = 0, buried = 0, outside = 0;
            foreach (var e in r.Entities)
            {
                int cx = e.X / 8, cy = e.Y / 8;
                if (e.X < 0 || e.Y < 0 || e.X > r.W || e.Y > r.H) { outside++; continue; }
                string? dir = EntityVisuals.SpikeDir(e.Name);
                if (dir != null)
                {
                    // keep only the runs whose base cell is solid, splitting the run where it floats
                    int len = Math.Max(1, (dir is "Up" or "Down" ? (e.W ?? 8) : (e.H ?? 8)) / 8);
                    int runStart = -1, keptCells = 0;
                    for (int i = 0; i <= len; i++)
                    {
                        bool ok = i < len && dir switch
                        {
                            "Up" => Solid(g, cx + i, cy),
                            "Down" => Solid(g, cx + i, cy - 1),
                            "Left" => Solid(g, cx, cy + i),
                            _ => Solid(g, cx - 1, cy + i),
                        };
                        if (ok && runStart < 0) runStart = i;
                        else if (!ok && runStart >= 0)
                        {
                            int runLen = i - runStart;
                            keptCells += runLen;
                            var part = new PcgEntity { Name = e.Name, Attrs = e.Attrs };
                            if (dir is "Up" or "Down") { part.X = e.X + runStart * 8; part.Y = e.Y; part.W = runLen * 8; }
                            else { part.X = e.X; part.Y = e.Y + runStart * 8; part.H = runLen * 8; }
                            kept.Add(part);
                            runStart = -1;
                        }
                    }
                    if (keptCells < len) floating++;
                    continue;
                }
                if (e.Name is "spinner" or "bigSpinner" or "refill" or "booster" or "infiniteStar")
                {
                    int sx = (e.X - 4) / 8, sy = (e.Y - 4) / 8; // point entities sit at the cell centre
                    if (Solid(g, sx, sy)) { buried++; continue; }
                    if (e.Name == "spinner" && Solid(g, sx - 1, sy) && Solid(g, sx + 1, sy) && Solid(g, sx, sy - 1) && Solid(g, sx, sy + 1)) { buried++; continue; }
                }
                kept.Add(e);
            }
            r.Entities = kept;
            if (floating > 0) fixes.Add($"{r.Name}: trimmed {floating} floating spike run(s)");
            if (buried > 0) fixes.Add($"{r.Name}: removed {buried} entities buried in solids");
            if (outside > 0) fixes.Add($"{r.Name}: removed {outside} entities outside the room");

            // spawns need ground under them
            int ledges = 0;
            foreach (var p in r.Entities.Where(e => e.Name == "player"))
            {
                int cx = Math.Clamp(p.X / 8, 0, w - 1), cy = Math.Clamp((p.Y - 1) / 8, 0, h - 1);
                if (cy + 1 < h && !Solid(g, cx, cy + 1))
                {
                    for (int dx = -1; dx <= 1; dx++)
                        if (cx + dx >= 0 && cx + dx < w && g[cy + 1][cx + dx] == '0') g[cy + 1][cx + dx] = r.Fg.SelectMany(s => s).FirstOrDefault(c => c != '0', '1');
                    ledges++;
                }
                for (int dy = 0; dy >= -1; dy--) if (cy + dy >= 0 && g[cy + dy][cx] != '0') g[cy + dy][cx] = '0';
            }
            if (ledges > 0) fixes.Add($"{r.Name}: added ground under {ledges} spawn(s)");

            // exit mouths: two cells of headroom inward, no hazards in them
            Store(r, g);
            var built = RoomAnalysis.Build(TempRoom(r));
            int mouths = 0;
            foreach (var ex in built.Exits)
            {
                var cells = ex.Cells(w, h).ToList();
                var inward = new List<(int X, int Y)>();
                foreach (var (x, y) in cells)
                    for (int d = 1; d <= 2; d++)
                        inward.Add(ex.Side switch { "left" => (x + d, y), "right" => (x - d, y), "top" => (x, y + d), _ => (x, y - d) });
                foreach (var (x, y) in inward)
                    if (x >= 0 && y >= 0 && x < w && y < h && g[y][x] != '0' && ex.Side is "left" or "right" && cells.Count <= 3) { g[y][x] = '0'; mouths++; }
                var mouth = new HashSet<(int, int)>(cells.Concat(inward));
                int removed = r.Entities.RemoveAll(e => IsHazard(e) && HazardCells(e).Any(mouth.Contains));
                mouths += removed;
            }
            if (mouths > 0) fixes.Add($"{r.Name}: cleared {mouths} cells/hazards at exit mouths");

            // hazard density toward the tier
            if (tier is int t && t is >= 1 and <= 5)
            {
                int passable = 0;
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (g[y][x] == '0') passable++;
                int removedHz = 0;
                while (passable > 0 && HazardCells(r) / (double)passable > TierHazardCap[t])
                {
                    var hz = r.Entities.Where(IsHazard).ToList();
                    if (hz.Count == 0) break;
                    r.Entities.Remove(hz[rng.Next(hz.Count)]);
                    removedHz++;
                }
                if (removedHz > 0) fixes.Add($"{r.Name}: removed {removedHz} hazards to fit {ReferenceMaps.TierNames[t]} density");
            }
            Store(r, g);
        }
        return fixes;
    }

    private static bool IsHazard(PcgEntity e) => EntityVisuals.SpikeDir(e.Name) != null || e.Name == "spinner";

    /// <summary>The cells a generated hazard occupies (spikes sit on the far side of their base; spinners are centred in a cell).</summary>
    private static IEnumerable<(int X, int Y)> HazardCells(PcgEntity e)
    {
        string? dir = EntityVisuals.SpikeDir(e.Name);
        if (dir == null) { yield return ((e.X - 4) / 8, (e.Y - 4) / 8); yield break; }
        int cx = e.X / 8, cy = e.Y / 8, len = Math.Max(1, (dir is "Up" or "Down" ? (e.W ?? 8) : (e.H ?? 8)) / 8);
        for (int i = 0; i < len; i++)
            yield return dir switch { "Up" => (cx + i, cy - 1), "Down" => (cx + i, cy), "Left" => (cx - 1, cy + i), _ => (cx, cy + i) };
    }

    private static int HazardCells(PcgRoom r) => r.Entities.Where(IsHazard).Sum(e => Math.Max(1, ((e.W ?? 8) / 8) * ((e.H ?? 8) / 8)));

    // ---- preview ----

    /// <summary>The whole generated level as one image, rooms at their relative positions.</summary>
    public static Bitmap Preview(PcgResult res, Autotiler? fg, Autotiler? bg, int maxSide = 1600)
    {
        var map = new MapBin { Root = new BinElement("Map") };
        var added = MapModel.ImportPcg(map, res, "", Point.Empty);
        var rooms = added.Select(l => new Room(l)).ToList();
        var bounds = MapModel.MapBounds(map);
        float scale = Math.Min(1f, maxSide / (float)Math.Max(1, Math.Max(bounds.Width, bounds.Height)));
        int ow = Math.Max(1, (int)(bounds.Width * scale)), oh = Math.Max(1, (int)(bounds.Height * scale));
        var bmp = new Bitmap(ow, oh, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(24, 24, 32));
        g.InterpolationMode = scale < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
        using var font = new Font("Segoe UI", 9f);
        foreach (var r in rooms)
        {
            using var img = RoomRenderJob.From(r, fg ?? Autotiler.For(null, true, null), bg ?? Autotiler.For(null, false, null)).Render();
            var dest = new RectangleF((r.X - bounds.X) * scale, (r.Y - bounds.Y) * scale, r.W * scale, r.H * scale);
            foreach (var layer in new[] { RoomImage.Level(img.Bg, 0), RoomImage.Level(img.Fg, 0) })
                if (layer != null) g.DrawImage(layer, dest);
            foreach (var e in r.EntitiesEl.Children)
            {
                var b = MapModel.Box(r, e, false);
                var rect = new RectangleF((b.X - bounds.X) * scale, (b.Y - bounds.Y) * scale, Math.Max(2, b.Width * scale), Math.Max(2, b.Height * scale));
                Color c = MapModel.IsSpawn(e) ? Color.LimeGreen : EntityVisuals.SpikeDir(e.Name) != null || e.Name == "spinner" ? Color.FromArgb(255, 80, 80) : Color.Gold;
                using var brush = new SolidBrush(Color.FromArgb(110, c));
                g.FillRectangle(brush, rect);
            }
            using var pen = new Pen(Color.FromArgb(200, 255, 255, 255));
            g.DrawRectangle(pen, dest.X, dest.Y, dest.Width, dest.Height);
            g.DrawString(r.Name, font, Brushes.White, dest.X + 2, dest.Y + 2);
        }
        return bmp;
    }

    // ---- corpora ----

    /// <summary>Rooms of a map as training data. Every solid fg glyph becomes '1' and unknown bg glyphs '1', so custom tilesets train too.</summary>
    public static IEnumerable<CorpusRoom> Corpus(MapBin map, int minCells = 9)
    {
        foreach (var l in MapModel.Levels(map).Children)
        {
            var r = new Room(l);
            if (r.Cols * r.Rows < minCells) continue;
            var fg = r.Grid(true);
            var bg = r.Grid(false);
            var fgRows = new string[r.Rows];
            var bgRows = new string[r.Rows];
            var sbF = new System.Text.StringBuilder(r.Cols);
            var sbB = new System.Text.StringBuilder(r.Cols);
            bool anyBg = false;
            for (int y = 0; y < r.Rows; y++)
            {
                sbF.Clear(); sbB.Clear();
                for (int x = 0; x < r.Cols; x++)
                {
                    sbF.Append(fg[x, y] == '0' ? '0' : '1');
                    char b = bg[x, y];
                    bool valid = b == '0' || (b >= '1' && b <= '9') || (b >= 'a' && b <= 'e');
                    sbB.Append(valid ? b : '1');
                    if (b != '0') anyBg = true;
                }
                fgRows[y] = sbF.ToString();
                bgRows[y] = sbB.ToString();
            }
            var cr = new CorpusRoom { Name = r.Name, FgRows = fgRows, BgRows = anyBg ? bgRows : null };
            foreach (var e in r.EntitiesEl.Children)
            {
                bool? red = e.Name == "booster" ? e.Str("red").Equals("true", StringComparison.OrdinalIgnoreCase) : null;
                cr.Entities.Add((e.Name, e.Num("x"), e.Num("y"), e.Num("width") > 0 ? e.Num("width") : null, e.Num("height") > 0 ? e.Num("height") : null, red));
            }
            yield return cr;
        }
    }
}
