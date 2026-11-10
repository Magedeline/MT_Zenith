using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

/// <summary>MCP prompts (prompts/list, prompts/get): reusable workflows for map-design agents, from one room to a whole level.</summary>
public static class Prompts
{
    /// <summary>
    /// The strongest-effort directive the agent panel prepends to every prompt in that mode: plan, build in big verified
    /// steps, never stop at a half-built map.
    /// </summary>
    public const string StrongestDirective =
        "STRONGEST EFFORT MODE. You are building a complete, playable Celeste map and the bar is a published collab room set. " +
        "Work autonomously to the end of the task; do not stop to ask unless an instruction is genuinely ambiguous. " +
        "Method: (1) read design_notes, then write/refresh concept, tier, mechanics, plan (room list with purpose, exits and mechanics) and todo; " +
        "(2) study 2-3 reference rooms of the target tier (difficulty_profile, study_reference_map render=true) before the first room; " +
        "(3) build each room in ONE build_room call (full fg rows or ops, exits, entities, spawn) or a batch, then analyze_room: every exit must be reachable " +
        "with the intended tech, items reachable, hazard pressure in the tier's band; fix and re-check before the next room; " +
        "(4) place rooms edge to edge (build_room attach) and carve openings with connect_rooms; run analyze_map detailed=true every few rooms and fix every issue; " +
        "(5) after all rooms: validate_map, analyze_map, compare_to_tier, render_room of the weakest rooms, then polish (refills, checkpoints, decals, bg tiles, music attrs); " +
        "(6) keep design_notes todo current and log decisions; (7) save_map when the plan is done and verified, then report what was built with the verification results. " +
        "Prefer high-level tools (build_room, draw_tiles, batch, connect_rooms, pcg_generate) over many tiny edits. Never leave a room without a spawn or an exit that is unreachable.";

    public static JsonArray List() => new()
    {
        new JsonObject
        {
            ["name"] = "build_map",
            ["title"] = "Build a complete level (strongest effort)",
            ["description"] = "Plan, build, verify and polish a whole multi-room Celeste level in the open map at a difficulty tier: notes, reference study, " +
                              "room-by-room construction with analyze_room, connectivity with analyze_map, tier calibration and polish.",
            ["arguments"] = new JsonArray
            {
                Arg("tier", "beginner, intermediate, advanced, expert or grandmaster (or 1-5).", true),
                Arg("theme", "Theme, mechanic focus or story beat (e.g. 'frozen ruins, dream blocks into spring chains').", false),
                Arg("rooms", "How many rooms (default 8).", false),
                Arg("mechanics", "Mechanics to feature (comma list), e.g. 'dream blocks, zip movers, spinners'.", false),
                Arg("start_from", "scratch (default) or pcg: seed the layout with pcg_generate and refine every room by hand.", false),
            }
        },
        new JsonObject
        {
            ["name"] = "design_level",
            ["title"] = "Design rooms for a difficulty tier",
            ["description"] = "Study installed collab maps at a tier, then build and verify a few original rooms in the open map.",
            ["arguments"] = new JsonArray
            {
                Arg("tier", "beginner, intermediate, advanced, expert or grandmaster (or 1-5).", true),
                Arg("theme", "Theme, mechanic focus or story beat for the rooms.", false),
                Arg("rooms", "How many rooms to build (default 3).", false),
            }
        },
        new JsonObject
        {
            ["name"] = "review_difficulty",
            ["title"] = "Review the open map's difficulty",
            ["description"] = "Compare the open map with a tier's statistics and movement-model analysis and propose concrete room changes.",
            ["arguments"] = new JsonArray { Arg("tier", "Target tier (beginner .. grandmaster).", true) }
        },
        new JsonObject
        {
            ["name"] = "fix_room",
            ["title"] = "Fix a room until it verifies",
            ["description"] = "Analyze one room, repair unreachable exits/items, spawn and hazard problems, and re-verify.",
            ["arguments"] = new JsonArray { Arg("room", "Room name.", true), Arg("tier", "Intended tier (optional).", false) }
        },
        new JsonObject
        {
            ["name"] = "polish_map",
            ["title"] = "Polish and finish the open map",
            ["description"] = "Final pass: connectivity, checkpoints and refills, visual polish (bg tiles, decals), room attributes, validation and save.",
            ["arguments"] = new JsonArray()
        },
        new JsonObject
        {
            ["name"] = "pcg_level",
            ["title"] = "Generate a level with PCG and refine it",
            ["description"] = "Use pcg_generate (candidates, repair, tier) to lay out a connected level, import it, then hand-refine every room with the movement model.",
            ["arguments"] = new JsonArray
            {
                Arg("tier", "Target tier.", true), Arg("rooms", "Rooms to generate (default 7).", false),
                Arg("model", "Markov model (pcg_models), default pe_d5_full; train one with pcg_train for a tier's style.", false),
            }
        },
    };

    private static JsonObject Arg(string name, string desc, bool required) =>
        new() { ["name"] = name, ["description"] = desc, ["required"] = required };

    public static JsonObject Get(string name, JsonObject? args)
    {
        string? A(string k) => args?[k]?.ToString() is { Length: > 0 } s ? s.Trim() : null;
        int tier = name is "polish_map" || (name == "fix_room" && A("tier") == null) ? 0 : ReferenceMaps.ParseTier(A("tier"));
        string tierName = ReferenceMaps.TierNames[tier];
        string text = name switch
        {
            "build_map" => BuildMap(tier, tierName, A("theme"), A("rooms"), A("mechanics"), A("start_from")),
            "design_level" => DesignLevel(tier, tierName, A("theme"), A("rooms")),
            "review_difficulty" => ReviewDifficulty(tier, tierName),
            "fix_room" => FixRoom(A("room") ?? throw new ToolException("fix_room needs a room."), tier),
            "polish_map" => PolishMap(),
            "pcg_level" => PcgLevel(tier, tierName, A("rooms"), A("model")),
            _ => throw new ToolException("Unknown prompt: " + name)
        };
        return new JsonObject
        {
            ["description"] = $"{name}{(tier > 0 ? $" ({tierName})" : "")}",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } }
            }
        };
    }

    private static string An(int tier) => tier is 2 or 3 or 4 ? "an" : "a";

    public static string BuildMap(int tier, string tierName, string? theme, string? rooms, string? mechanics, string? startFrom)
    {
        int n = int.TryParse(rooms, out int r) && r > 0 ? Math.Min(r, 40) : 8;
        bool pcg = string.Equals(startFrom, "pcg", StringComparison.OrdinalIgnoreCase);
        return $"""
            Build a complete {tierName}-difficulty Celeste level of {n} rooms in the open map{(theme != null ? $", themed: {theme}" : "")}{(mechanics != null ? $", featuring: {mechanics}" : "")}.
            Work at the strongest effort you can: plan first, build in large verified steps, and do not stop until every room verifies and the map is saved.

            PHASE 0 - Notes. design_notes read. Then write: concept (2-3 sentences), tier ({tierName.ToLowerInvariant()}), mechanics (which, and how they escalate), plan (one line per room: name, purpose, size tier, exits, mechanics, checkpoint?), todo.
            PHASE 1 - Study. difficulty_profile "{tierName.ToLowerInvariant()}" for sizes/densities/common entities; study_reference_map (overview, then 2-3 rooms with render=true) of two example maps. Note spacing, hazard placement, refill rhythm and how mechanics are introduced. Never copy their rooms.
            PHASE 2 - Skeleton. {(pcg ? "pcg_generate with candidates=6, repair=true, tier set, preview=true (train a model with pcg_train on this tier's reference maps first if time allows), then pcg_import. Treat the result as a draft: every room is rebuilt or heavily edited." : "Decide the room chain and its shape on the world grid (left-to-right, climbs, drops); rooms touch edge to edge. The first room has the start spawn; the last has the goal (e.g. blackGem crystal heart or a goldenBerry).")}
            PHASE 3 - Rooms. For each room, in plan order: build_room in ONE call (attach to the previous room's side, size, fill border, fg rows or ops for the geometry, exits, entities with attrs, spawn), then connect_rooms with the previous room, then analyze_room. Required: every exit reachable with the intended tech (none/dash for {tierName}{(tier >= 4 ? ", advanced allowed" : "")}), items reachable, no 'unreachable' exits, hazard pressure in the tier band, a spawn in every room, no overlaps. Fix and re-run analyze_room until clean. render_room when geometry matters. Use batch for multi-step edits.
            Guidance for {tierName}: {ReferenceMaps.GuidanceFor(tier)}
            PHASE 4 - Whole map. analyze_map detailed=true: fix blocked/void openings, unreachable rooms and rooms with no reachable exit. validate_map. compare_to_tier "{tierName.ToLowerInvariant()}" and adjust outliers (spacing, refills, checkpoints, room sizes). Add respawn points (player entities) where a death would send the player too far back.
            PHASE 5 - Polish. bg tiles (set_tiles layer=bg / draw_tiles) for depth, decals where the editor has them, room attrs (music, windPattern, dark) that fit the theme, a readable intended route in every room. Re-run analyze_map and validate_map.
            PHASE 6 - Finish. design_notes: update plan/todo/log. save_map. Report: room list with purpose and required tech, verification results (exits reachable, map traversable, tier comparison), and what a human playtest should look at.
            """;
    }

    public static string DesignLevel(int tier, string tierName, string? theme, string? rooms)
    {
        int n = int.TryParse(rooms, out int r) && r > 0 ? Math.Min(r, 20) : 3;
        return $"""
            Design {n} new room(s) for {An(tier)} {tierName}-difficulty Celeste level in the open map{(theme != null ? $", themed around: {theme}" : "")}.

            1. design_notes read; add a short plan (one line per room: purpose, exits, mechanics).
            2. difficulty_profile "{tierName.ToLowerInvariant()}" for typical sizes, hazard/refill densities, checkpoint frequency and common entities.
            3. study_reference_map on 2-3 of its example maps: overview, then a few rooms with render=true. Note pacing, spacing, how mechanics are introduced and where refills and spawns sit.
            4. Build each room with ONE build_room call (attach to the previous room, fill border, ops/fg rows, exits, entities, spawn), connect_rooms to its neighbour, then analyze_room and fix until every exit is reachable with the intended tech and hazard pressure fits. Guidance: {ReferenceMaps.GuidanceFor(tier)}
            5. render_room each room, validate_map, analyze_map detailed=true, then compare_to_tier "{tierName.ToLowerInvariant()}" and adjust anything far outside the typical range.
            6. Summarize what you built (with the analyze_room verdicts) and ask before calling save_map.

            The reference maps belong to other creators: learn from them, but do not reproduce their rooms or layouts.
            """;
    }

    public static string ReviewDifficulty(int tier, string tierName) => $"""
        Review whether the open map fits {tierName} difficulty.
        1. compare_to_tier "{tierName.ToLowerInvariant()}" and difficulty_profile for context; analyze_map detailed=true for connectivity.
        2. analyze_room on every room (or the outliers): required tech per exit, hazard pressure, unreachable items; render_room where unclear.
        3. Report the mismatches and propose specific edits per room (spacing, hazards, refills, spawns, room size). Don't edit until I agree.
        Guidance: {ReferenceMaps.GuidanceFor(tier)}
        """;

    public static string FixRoom(string room, int tier) => $"""
        Make room "{room}" verify.
        1. analyze_room "{room}" render=true and get_room. List every problem: unreachable or advanced-only exits{(tier > 0 && tier < 4 ? " (not acceptable at " + ReferenceMaps.TierNames[tier] + ")" : "")}, unreachable items, missing spawn, hazards blocking the route, dead pockets.
        2. Fix with draw_tiles (platforms, ledges, exit mouths), update_entity/remove_entity/add_entity (refills, springs, spikes), connect_rooms if a neighbour is blocked. Prefer one batch.
        3. analyze_room again until every exit shows the intended min_tech and no item is unreachable; then render_room and summarize the changes.
        """;

    public static string PolishMap() => """
        Finish the open map.
        1. analyze_map detailed=true and validate_map: fix every issue (blocked/void openings, rooms not traversable, missing spawns, overlaps, ids).
        2. Checkpoints and refills: a spawn after each hard section, refills where the route needs a second dash; analyze_room to confirm items are reachable.
        3. Visual polish: bg layer tiles (draw_tiles layer=bg) behind play areas, varied fg tilesets where the theme asks (list_tilesets), decals if present, room attrs (music, dark, windPattern) consistent per section.
        4. compare_to_tier for the intended tier; adjust outliers.
        5. design_notes: log what changed; save_map; report the verification results.
        """;

    public static string PcgLevel(int tier, string tierName, string? rooms, string? model) => $"""
        Generate {An(tier)} {tierName} level with the procedural generator, then refine it by hand.
        1. pcg_models. If no custom model fits, consider pcg_train name="{tierName.ToLowerInvariant()}_fg" source=reference tier="{tierName.ToLowerInvariant()}" (and a bg model) so the sampler learns this tier's shapes.
        2. pcg_generate rooms={(int.TryParse(rooms, out int n) && n > 0 ? n : 7)} candidates=6 repair=true tier="{tierName.ToLowerInvariant()}"{(model != null ? $" model=\"{model}\"" : "")} preview=true. Read the report: rooms_fully_traversable, exits_reachable, tier_feel, room_details hints.
        3. pcg_import (prefix pcg_). analyze_map detailed=true.
        4. For each generated room: analyze_room; rebuild the weak parts with draw_tiles / entities so every exit is reachable with the intended tech and the room has a clear route and idea; the generator gives you shapes, you give them intent.
        5. validate_map, compare_to_tier, polish, save_map; report what changed per room.
        """;
}
