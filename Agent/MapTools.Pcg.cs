using System.Drawing.Imaging;
using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

// Procedural generation tools: PCGHelper's seleCte Markov generator with candidate search, repair, tier shaping,
// movement-model judgement, previews, import into the open map and training of new models from reference maps.
public sealed partial class MapTools
{
    private PcgGenerator? _pcg;
    private readonly object _pcgGate = new();
    private readonly Dictionary<string, PcgCandidate> _pcgResults = new();
    private string? _pcgLastId;

    private void RegisterPcg()
    {
        Add("pcg_models", "Procedural generation (PCGHelper seleCte): is it available, which Markov models exist (shipped vanilla ones and custom models " +
            "trained with pcg_train), background models, solid tileset glyphs and the mechanic groups pcg_generate can filter.", true, new(), None, _ => PcgModels());
        Add("pcg_generate", "Generate a connected level with the seleCte Markov generator and judge it with the movement model, WITHOUT touching the map " +
            "(pcg_import places it). Generates 'candidates' variants of the seed and keeps the best-scoring one (traversability, hazard pressure vs the target " +
            "tier, entity variety). repair=true trims floating spikes, buried spinners, groundless spawns and blocked exit mouths and caps hazards to the tier. " +
            "Returns the report and a generation_id; preview=true also returns an image of the level.", true,
            Props(("seed", P("string", "Seed (blank = random; reported back).")),
                  ("rooms", P("integer", "Rooms to generate (default 7, max 60).")),
                  ("room_w", P("integer", "Room width in tiles (default 40).")), ("room_h", P("integer", "Room height in tiles (default 23).")),
                  ("proba", P("number", "Branching probability 0..1 (0 = corridor, 1 = labyrinth; default 0.3).")),
                  ("model", P("string", "Foreground model (pcg_models; default pe_d5_full = all vanilla rooms).")),
                  ("background", P("string", "Background model or 'none' (default none).")),
                  ("solid", P("string", "Tileset glyph drawn for solids (default '1' dirt).")),
                  ("tries", P("integer", "Resamples per room before giving up (default 10).")),
                  ("bt_depth", P("integer", "Markov backtracking depth 0-10 (default 0).")),
                  ("reset_skeleton", P("boolean", "Redraw the whole skeleton when a room never comes out playable (default false).")),
                  ("candidates", P("integer", "Variants to generate and score (default 1, max 24).")),
                  ("repair", P("boolean", "Clean sampler artefacts (default true).")),
                  ("tier", TierP),
                  ("mechanics", ArrayP("Allowed mechanics: labels (spikes, spinners, bumpers, dream blocks, crumble blocks, jump-throughs, boosters, water, refills, feathers) or entity names. Default: all.", new JsonObject { ["type"] = "string" })),
                  ("preview", P("boolean", "Return a rendered image of the level (default false).")),
                  ("analyze", P("boolean", "Judge every room with the movement model (default true)."))), None, PcgGenerate);
        Add("pcg_import", "Add a generated level (from pcg_generate) to the open map as new rooms in one undo step: at x/y, or right of the existing rooms. " +
            "Rooms keep their relative layout; names get the prefix. Then connect it to your rooms with connect_rooms and refine with the other tools.", false,
            Props(("generation_id", P("string", "From pcg_generate (default: the last one).")),
                  ("prefix", P("string", "Room name prefix (default 'pcg_').")),
                  ("x", P("integer", "World x of the level's top-left (default right of the map).")), ("y", P("integer", "World y."))), None, PcgImport);
        Add("pcg_train", "Train a new seleCte Markov model from rooms and save it under a name usable as pcg_generate's model/background: " +
            "from the open map, or from installed reference maps filtered by difficulty tier / substring / explicit keys (list_reference_maps). " +
            "Foreground models learn layout + spikes/spinners/boosters/refills/etc. from the chosen rooms, so a model trained on Strawberry Jam Expert maps " +
            "generates expert-shaped rooms. Takes seconds to a few minutes depending on the room count.", true,
            Props(("name", P("string", "Model name (letters, digits, _ -).")),
                  ("layer", Enum("Which layer to learn.", "fg", "bg")),
                  ("source", Enum("Where the rooms come from.", "open_map", "reference")),
                  ("tier", TierP), ("filter", P("string", "Substring of the reference map key (source mod or path).")),
                  ("maps", ArrayP("Explicit reference map keys/names.", new JsonObject { ["type"] = "string" })),
                  ("max_rooms", P("integer", "Cap on rooms used (default 1500).")),
                  ("overwrite", P("boolean", "Replace an existing custom model of that name (default false)."))), new[] { "name" }, PcgTrain);
    }

    private PcgGenerator Pcg()
    {
        lock (_pcgGate)
        {
            if (_pcg != null) return _pcg;
            string? root = PcgGenerator.FindLoennRoot(_host.Invoke(() => ModDir(_host.Path)));
            if (root == null) throw new ToolException("PCGHelper not found. Install the PCGHelper mod next to the CEL mod (Mods/PCGHelper with Loenn/library/selecte), " +
                                                    "or set MT_ZENITH_PCGHELPER to its folder, or use PCG > Locate PCGHelper in the GUI.");
            return _pcg = new PcgGenerator(root);
        }
    }

    private ToolResult PcgModels()
    {
        string? root = null;
        string? err = null;
        try { root = Pcg().LoennRoot; } catch (ToolException ex) { err = ex.Message; }
        var o = new JsonObject { ["available"] = root != null, ["loenn_root"] = root };
        if (err != null) o["error"] = err;
        var models = new JsonArray();
        foreach (string m in PcgGenerator.Models)
            models.Add(new JsonObject { ["name"] = m, ["layer"] = "fg", ["builtin"] = true, ["about"] = m == "pe_d5_full" ? "all 824 vanilla rooms" : $"vanilla chapter {m[3..]} only" });
        var backgrounds = new JsonArray();
        foreach (string b in PcgGenerator.Backgrounds) backgrounds.Add(new JsonObject { ["name"] = b, ["builtin"] = true });
        foreach (var c in PcgGenerator.CustomModels())
        {
            var cj = new JsonObject { ["name"] = c.Name, ["layer"] = c.Layer, ["builtin"] = false, ["patterns"] = c.Patterns, ["rooms"] = c.Rooms, ["sources"] = c.Sources };
            if (c.Layer == "bg") backgrounds.Add(cj); else models.Add(cj);
        }
        o["models"] = models;
        o["backgrounds"] = backgrounds;
        if (root != null)
        {
            try { o["solid_tilesets"] = new JsonArray(Pcg().SolidTilesets().Select(t => (JsonNode)$"{t.Glyph} = {t.Name}").ToArray()); }
            catch (Exception ex) { o["solid_tilesets_error"] = ex.Message; }
        }
        o["mechanics"] = new JsonArray(PcgPipeline.Mechanics.Select(m => (JsonNode)$"{m.Label}: {string.Join(", ", m.Names)}").ToArray());
        o["tier_hazard_pressure_targets"] = new JsonObject(Enumerable.Range(1, 5).Select(t => KeyValuePair.Create(ReferenceMaps.TierNames[t].ToLowerInvariant(), (JsonNode?)PcgPipeline.TierPressure[t])));
        o["custom_models_dir"] = PcgGenerator.CustomModelsDir;
        return ToolResult.Json(o);
    }

    private ToolResult PcgGenerate(JsonObject a)
    {
        var gen = Pcg();
        var o = new PcgPipelineOptions
        {
            Base = new PcgOptions
            {
                Seed = S(a, "seed") ?? "",
                NbRooms = Math.Clamp(I(a, "rooms") ?? 7, 1, 60),
                RoomW = Math.Clamp(I(a, "room_w") ?? 40, 8, 200),
                RoomH = Math.Clamp(I(a, "room_h") ?? 23, 8, 200),
                Proba = Math.Clamp(D(a, "proba") ?? 0.3, 0, 1),
                Model = S(a, "model") is { Length: > 0 } m ? m : "pe_d5_full",
                Background = S(a, "background") is { Length: > 0 } b ? b : "none",
                Solid = S(a, "solid") is { Length: 1 } s ? s : "1",
                TriesLimit = Math.Clamp(I(a, "tries") ?? 10, 1, 100),
                BtDepth = Math.Clamp(I(a, "bt_depth") ?? 0, 0, 10),
                ResetSkeleton = B(a, "reset_skeleton"),
            },
            Candidates = Math.Clamp(I(a, "candidates") ?? 1, 1, 24),
            Repair = B(a, "repair", true),
            TargetTier = S(a, "tier") is { Length: > 0 } t ? ReferenceMaps.ParseTier(t) : null,
            Allowed = PcgPipeline.ParseAllowed((a["mechanics"] as JsonArray)?.Select(n => n?.ToString() ?? "")),
            Analyze = B(a, "analyze", true),
        };
        var log = new List<string>();
        PcgCandidate cand;
        try { cand = PcgPipeline.Run(gen, o, log.Add); }
        catch (MoonSharp.Interpreter.InterpreterException ex) { throw new ToolException("seleCte failed: " + (ex.DecoratedMessage ?? ex.Message)); }
        string id = Guid.NewGuid().ToString("N")[..8];
        lock (_pcgGate)
        {
            _pcgResults[id] = cand;
            _pcgLastId = id;
            while (_pcgResults.Count > 6) _pcgResults.Remove(_pcgResults.Keys.First());
        }
        var rep = cand.Report.Count > 0 ? cand.Report : new JsonObject { ["seed"] = cand.Result.Seed, ["rooms"] = cand.Result.Rooms.Count, ["route"] = string.Join(" -> ", cand.Result.Route), ["selecte_route_playable"] = cand.Result.AllPlayable };
        rep["generation_id"] = id;
        rep["model"] = o.Base.Model;
        if (o.TargetTier is int tt) rep["target_tier"] = ReferenceMaps.TierNames[tt];
        rep["layout"] = new JsonArray(cand.Result.Rooms.Select(r => (JsonNode)$"{r.Name} at ({r.X},{r.Y}) {r.W}x{r.H}").ToArray());
        rep["next"] = "pcg_import places these rooms into the open map (same relative layout); then connect_rooms to your rooms and refine.";
        if (log.Count > 0) rep["log"] = string.Join("\n", log.TakeLast(12));
        var res = ToolResult.Json(rep);
        if (B(a, "preview"))
        {
            var (fg, bg) = _host.Invoke(() => (_host.FgTiler, _host.BgTiler));
            using var bmp = PcgPipeline.Preview(cand.Result, fg, bg);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            res.AddText($"Preview of seed \"{cand.Result.Seed}\" ({bmp.Width}x{bmp.Height}): red = spikes/spinners, green = spawns, gold = other entities.");
            res.AddPng(ms.ToArray());
        }
        return res;
    }

    private ToolResult PcgImport(JsonObject a)
    {
        PcgCandidate cand;
        lock (_pcgGate)
        {
            string? id = S(a, "generation_id") ?? _pcgLastId;
            if (id == null || !_pcgResults.TryGetValue(id, out cand!)) throw new ToolException("No generation found; call pcg_generate first (or pass its generation_id).");
        }
        string prefix = S(a, "prefix") ?? "pcg_";
        Point? at = I(a, "x") is int x && I(a, "y") is int y ? new Point(MapModel.Snap8(x), MapModel.Snap8(y)) : null;
        return Edit(() =>
        {
            var map = MapOrThrow();
            var added = MapModel.ImportPcg(map, cand.Result, prefix, at);
            var rooms = added.Select(l => new Room(l)).ToList();
            var bounds = rooms.Select(r => r.Bounds).Aggregate(Rectangle.Union);
            var overlaps = MapModel.Levels(map).Children.Select(l => new Room(l)).Where(r => !added.Contains(r.El) && rooms.Any(n => n.Bounds.IntersectsWith(r.Bounds))).Select(r => r.Name).ToList();
            string msg = $"Imported {rooms.Count} generated rooms (seed \"{cand.Result.Seed}\") at ({bounds.X},{bounds.Y}) {bounds.Width}x{bounds.Height}: {string.Join(", ", rooms.Select(r => r.Name))}. " +
                         $"Route: {string.Join(" -> ", cand.Result.Route.Select(n => prefix + n))}.";
            if (overlaps.Count > 0) msg += $" WARNING: overlaps existing rooms {string.Join(", ", overlaps)}; move them (update_room) or redo with a different x/y.";
            return (null, msg);
        });
    }

    private ToolResult PcgTrain(JsonObject a)
    {
        var gen = Pcg();
        string name = Req(a, "name");
        string layer = S(a, "layer") == "bg" ? "bg" : "fg";
        string source = S(a, "source") ?? (S(a, "tier") != null || S(a, "filter") != null || a["maps"] != null ? "reference" : "open_map");
        int max = Math.Clamp(I(a, "max_rooms") ?? 1500, 1, 20000);
        if (PcgGenerator.CustomModel(name) != null && !B(a, "overwrite")) throw new ToolException($"A custom model '{name}' exists; pass overwrite=true to replace it.");

        var rooms = new List<CorpusRoom>();
        string sources;
        if (source == "open_map")
        {
            rooms.AddRange(_host.Invoke(() => PcgPipeline.Corpus(MapOrThrow()).ToList()));
            sources = "open map " + System.IO.Path.GetFileName(_host.Invoke(() => _host.Path) ?? "(unsaved)");
        }
        else
        {
            int? tier = S(a, "tier") is { Length: > 0 } t ? ReferenceMaps.ParseTier(t) : null;
            string filter = S(a, "filter") ?? "";
            var keys = (a["maps"] as JsonArray)?.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList();
            List<RefMap> maps;
            if (keys is { Count: > 0 }) maps = keys.Select(ReferenceMaps.Find).ToList();
            else maps = ReferenceMaps.All().Where(m => m.Kind == "map" && (tier == null || m.Tier == tier) && (filter.Length == 0 || m.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
            if (maps.Count == 0) throw new ToolException("No reference maps match; see list_reference_maps.");
            foreach (var m in maps)
            {
                if (rooms.Count >= max) break;
                try { rooms.AddRange(PcgPipeline.Corpus(ReferenceMaps.Load(m)).Take(max - rooms.Count)); }
                catch (Exception) { /* unreadable map */ }
            }
            sources = $"{maps.Count} reference maps" + (tier is int ti ? $", tier {ReferenceMaps.TierNames[ti]}" : "") + (filter.Length > 0 ? $", filter '{filter}'" : "");
        }
        if (rooms.Count == 0) throw new ToolException("No rooms to train on.");
        TrainResult tr;
        var log = new List<string>();
        try { tr = gen.Train(rooms, layer, log.Add); }
        catch (MoonSharp.Interpreter.InterpreterException ex) { throw new ToolException("Training failed: " + (ex.DecoratedMessage ?? ex.Message)); }
        PcgModelInfo info;
        try { info = PcgGenerator.SaveModel(name, layer, tr, sources); }
        catch (ArgumentException ex) { throw new ToolException(ex.Message); }
        return ToolResult.Json(new JsonObject
        {
            ["model"] = info.Name, ["layer"] = layer, ["file"] = info.Path,
            ["rooms_used"] = tr.RoomsKept, ["rooms_total"] = tr.RoomsTotal, ["entities_stamped"] = tr.Entities, ["patterns"] = tr.Patterns,
            ["sources"] = sources,
            ["skipped"] = tr.Skipped.Count,
            ["next"] = $"pcg_generate with {(layer == "fg" ? "model" : "background")}='{info.Name}'.",
            ["log"] = string.Join("\n", log.TakeLast(6)),
        });
    }
}
