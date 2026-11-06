using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

/// <summary>MCP prompts (prompts/list, prompts/get): reusable workflows for map-design agents.</summary>
public static class Prompts
{
    public static JsonArray List() => new()
    {
        new JsonObject
        {
            ["name"] = "design_level",
            ["title"] = "Design a level for a difficulty tier",
            ["description"] = "Study installed collab maps (Strawberry Jam, Spring Collab, ...) at a difficulty from beginner to grandmaster, then build and check original rooms in the open map.",
            ["arguments"] = new JsonArray
            {
                Arg("tier", "beginner, intermediate, advanced, expert or grandmaster (or 1-5).", true),
                Arg("theme", "Theme, mechanic focus or story beat for the level.", false),
                Arg("rooms", "How many rooms to build (default 3).", false),
            }
        },
        new JsonObject
        {
            ["name"] = "review_difficulty",
            ["title"] = "Review the open map's difficulty",
            ["description"] = "Compare the open map with a tier's statistics and suggest concrete room changes.",
            ["arguments"] = new JsonArray { Arg("tier", "Target tier (beginner .. grandmaster).", true) }
        },
    };

    private static JsonObject Arg(string name, string desc, bool required) =>
        new() { ["name"] = name, ["description"] = desc, ["required"] = required };

    public static JsonObject Get(string name, JsonObject? args)
    {
        string? A(string k) => args?[k]?.ToString() is { Length: > 0 } s ? s.Trim() : null;
        int tier = ReferenceMaps.ParseTier(A("tier"));
        string tierName = ReferenceMaps.TierNames[tier];
        string text = name switch
        {
            "design_level" => DesignLevel(tier, tierName, A("theme"), A("rooms")),
            "review_difficulty" => ReviewDifficulty(tier, tierName),
            _ => throw new ToolException("Unknown prompt: " + name)
        };
        return new JsonObject
        {
            ["description"] = $"{name} ({tierName})",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } }
            }
        };
    }

    public static string DesignLevel(int tier, string tierName, string? theme, string? rooms)
    {
        int n = int.TryParse(rooms, out int r) && r > 0 ? Math.Min(r, 20) : 3;
        return $"""
            Design {n} new room(s) for {(tier is 2 or 3 or 4 ? "an" : "a")} {tierName}-difficulty Celeste level in the open map{(theme != null ? $", themed around: {theme}" : "")}.

            1. Call difficulty_profile with tier "{tierName.ToLowerInvariant()}" to learn typical room sizes, hazard/refill densities, checkpoint frequency and common entities.
            2. Study 2-3 of its example maps with study_reference_map: first the map overview, then a few rooms with render=true. Note pacing, spacing, how mechanics are introduced and combined, and where refills and spawns sit.
            3. Plan each room in a sentence or two: the intended route, the mechanic(s) it tests and why that suits {tierName}. Guidance: {ReferenceMaps.GuidanceFor(tier)}
            add_entity (use list_placements for entity names). Give every room a player spawn and connect rooms edge to edge.
            5. render_room each room to check it, run validate_map, then compare_to_tier "{tierName.ToLowerInvariant()}" and adjust anything far outside the typical range.
            6. Summarize what you built and ask before calling save_map.

            The reference maps belong to other creators: learn from them, but do not reproduce their rooms or layouts.
            """;
    }

    public static string ReviewDifficulty(int tier, string tierName) => $"""
        Review whether the open map fits {tierName} difficulty.
        1. Call compare_to_tier with tier "{tierName.ToLowerInvariant()}" and difficulty_profile for context.
        2. Look at the rooms that drive outlying metrics (list_rooms, get_room, render_room).
        3. Report the mismatches and propose specific edits per room (spacing, hazards, refills, spawns, room size). Don't edit until I agree.
        Guidance: {ReferenceMaps.GuidanceFor(tier)}
        """;
}
