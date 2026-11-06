using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

// Reference-map study tools: read other installed maps (Strawberry Jam, Spring Collab, ...) and difficulty-tier statistics.
public sealed partial class MapTools
{
    private static JsonObject TierP => Enum("Difficulty tier.", "beginner", "intermediate", "advanced", "expert", "grandmaster");

    private void RegisterReference()
    {
        Add("list_reference_maps", "List installed maps from other mods (Strawberry Jam, Spring Collab, ...) and this mod, with collab difficulty tier " +
            "(Beginner..Grandmaster, from folders like '3-Advanced') and kind (map, lobby, gym, heartside). Study them, don't copy them.", true,
            Props(("tier", TierP), ("filter", P("string", "Substring of the source mod or path.")),
                  ("include_special", P("boolean", "Include lobbies, gyms and heart sides (default false).")),
                  ("limit", P("integer", "Max maps listed (default 60)."))), None, ListReferenceMaps);
        Add("study_reference_map", "Study a reference map: design statistics and room list, or with 'room' that room's entities, triggers, " +
            "fg tile grid and optionally a rendered PNG. Read-only; nothing is copied into the open map.", true,
            Props(("map", P("string", "Map key, path or name from list_reference_maps.")), ("room", RoomP),
                  ("render", P("boolean", "With room: also render it to PNG (default false).")),
                  ("include_tiles", P("boolean", "With room: include fg tile rows (default true).")),
                  ("entity_limit", P("integer", "With room: max entities listed in full (default 150; the rest are counted by type).")),
                  ("scale", P("number", "Render scale (default: fit to 1600px, max 2)."))), new[] { "map" }, StudyReferenceMap);
        Add("difficulty_profile", "Typical design statistics (p25/median/p75 per screen) of installed collab maps for a difficulty tier, the " +
            "most common entities, design guidance and example maps to study.", true,
            Props(("tier", TierP)), new[] { "tier" }, a => ToolResult.Json(ReferenceMaps.ProfileJson(ReferenceMaps.Profile(ReferenceMaps.ParseTier(Req(a, "tier")))))); 
        Add("compare_to_tier", "Compare the open map's statistics (size, hazards, refills, checkpoints, ...) with a difficulty tier's profile.", true,
            Props(("tier", TierP)), new[] { "tier" }, CompareToTier);
    }

    private ToolResult ListReferenceMaps(JsonObject a)
    {
        int? tier = S(a, "tier") is { Length: > 0 } t ? ReferenceMaps.ParseTier(t) : null;
        string? filter = S(a, "filter");
        bool special = B(a, "include_special");
        int limit = Math.Clamp(I(a, "limit") ?? 60, 1, 500);
        var all = ReferenceMaps.All();
        var maps = all.Where(m => (tier == null || m.Tier == tier) && (special || m.Kind == "map")
                                  && (string.IsNullOrEmpty(filter) || m.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
        var bySource = new JsonObject();
        foreach (var g in all.Where(m => m.Kind == "map").GroupBy(m => m.Source).OrderBy(g => g.Key))
        {
            var tiers = new JsonObject();
            foreach (var tg in g.Where(m => m.Tier != null).GroupBy(m => m.Tier!.Value).OrderBy(x => x.Key))
                tiers[ReferenceMaps.TierNames[tg.Key]] = tg.Count();
            bySource[g.Key] = tiers.Count > 0 ? new JsonObject { ["maps"] = g.Count(), ["by_tier"] = tiers } : new JsonObject { ["maps"] = g.Count() };
        }
        return ToolResult.Json(new JsonObject
        {
            ["sources"] = bySource,
            ["matched"] = maps.Count,
            ["maps"] = new JsonArray(maps.Take(limit).Select(m =>
            {
                var o = new JsonObject { ["map"] = m.Key };
                if (m.Tier is int ti) o["tier"] = ReferenceMaps.TierNames[ti];
                if (m.Kind != "map") o["kind"] = m.Kind;
                return (JsonNode)o;
            }).ToArray()),
            ["note"] = "These are other creators' maps: learn layout, pacing and difficulty from them, but build original rooms."
        });
    }

    private ToolResult StudyReferenceMap(JsonObject a)
    {
        var rm = ReferenceMaps.Find(Req(a, "map"));
        var map = ReferenceMaps.Load(rm);
        var head = new JsonObject { ["map"] = rm.Key };
        if (rm.Tier is int t) head["tier"] = ReferenceMaps.TierNames[t];
        if (rm.Kind != "map") head["kind"] = rm.Kind;

        string? roomName = S(a, "room");
        if (string.IsNullOrEmpty(roomName))
        {
            var st = ReferenceMaps.StatsOf(rm);
            foreach (var (k, v) in ReferenceMaps.StatsJson(st)) head[k] = v?.DeepClone();
            head["rooms"] = new JsonArray(MapModel.Levels(map).Children.Take(150).Select(el =>
            {
                var r = new Room(el);
                var ents = el.Child("entities")?.Children ?? new();
                return (JsonNode)new JsonObject
                {
                    ["name"] = r.Name, ["x"] = r.X, ["y"] = r.Y, ["w"] = r.W, ["h"] = r.H,
                    ["entities"] = ents.Count,
                    ["top"] = string.Join(", ", ents.Where(e => !MapModel.IsSpawn(e)).GroupBy(e => e.Name)
                        .OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key}×{g.Count()}")),
                };
            }).ToArray());
            if (rm.Tier is int tt) head["guidance"] = ReferenceMaps.GuidanceFor(tt);
            return ToolResult.Json(head);
        }

        string want = roomName.StartsWith("lvl_") ? roomName[4..] : roomName;
        var room = MapModel.Levels(map).Children.Select(el => new Room(el))
                       .FirstOrDefault(r => r.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ToolException($"Room '{roomName}' not found in {rm.Key}.");
        head["room"] = room.Name;
        head["x"] = room.X; head["y"] = room.Y; head["width"] = room.W; head["height"] = room.H;
        var ents = room.El.Child("entities")?.Children ?? new();
        int max = Math.Clamp(I(a, "entity_limit") ?? 150, 0, 2000);
        head["entities"] = new JsonArray(ents.Take(max).Select(e => (JsonNode)EntityJson(e, false)).ToArray());
        if (ents.Count > max)
            head["entities_omitted"] = string.Join(", ", ents.Skip(max).GroupBy(e => e.Name).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}?{g.Count()}"));
        head["triggers"] = new JsonArray((room.El.Child("triggers")?.Children ?? new()).Select(e => (JsonNode)EntityJson(e, true)).ToArray());
        if (B(a, "include_tiles", true)) head["fg"] = GridRows(room.Grid(true));
        var json = ToolResult.Json(head);
        if (!B(a, "render")) return json;

        string? modDir = GameAssets.ModsDir is { } md && Directory.Exists(Path.Combine(md, rm.Source)) ? Path.Combine(md, rm.Source) : null;
        var fg = Autotiler.For(MapModel.TilesetXml(map, true), true, modDir);
        var bg = Autotiler.For(MapModel.TilesetXml(map, false), false, modDir);
        RoomRenderJob job;
        List<(System.Drawing.RectangleF Box, string Label, int Kind)> boxes;
        lock (map) { job = RoomRenderJob.From(room, fg, bg); boxes = EntityBoxes(room); }
        var png = RenderPng(job, boxes, D(a, "scale"), $"{rm.Name} / {room.Name}");
        foreach (var c in png.Content) json.Content.Add(c?.DeepClone());
        return json;
    }

    private ToolResult CompareToTier(JsonObject a)
    {
        int tier = ReferenceMaps.ParseTier(Req(a, "tier"));
        var stats = _host.Invoke(() => ReferenceMaps.Analyze(MapOrThrow()));
        var profile = ReferenceMaps.Profile(tier);
        return ToolResult.Json(ReferenceMaps.Compare(stats, profile));
    }
}
