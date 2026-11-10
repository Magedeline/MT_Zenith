using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

public sealed class ToolResult
{
    public readonly JsonArray Content = new();
    public bool IsError;

    public static ToolResult Text(string text, bool error = false)
    {
        var r = new ToolResult { IsError = error };
        r.AddText(text);
        return r;
    }

    public static ToolResult Json(JsonNode node) => Text(node.ToJsonString(MapTools.JsonOut));

    public ToolResult AddText(string text)
    {
        Content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        return this;
    }

    public ToolResult AddPng(byte[] png) => AddImage(png, "image/png");

    public ToolResult AddImage(byte[] data, string mimeType)
    {
        Content.Add(new JsonObject { ["type"] = "image", ["mimeType"] = mimeType, ["data"] = Convert.ToBase64String(data) });
        return this;
    }

    /// <summary>MCP audio content (protocol 2025-03-26+), for clients/models that accept audio input.</summary>
    public ToolResult AddAudio(byte[] data, string mimeType)
    {
        Content.Add(new JsonObject { ["type"] = "audio", ["mimeType"] = mimeType, ["data"] = Convert.ToBase64String(data) });
        return this;
    }

    public JsonObject ToJson() => new() { ["content"] = Content.DeepClone(), ["isError"] = IsError };
}

/// <summary>A tool error the model should see (bad room name, read-only mode, ...).</summary>
public sealed class ToolException(string message) : Exception(message);

public sealed record ToolDef(string Name, string Description, JsonObject InputSchema, bool ReadOnly, Func<JsonObject, ToolResult> Run);

/// <summary>
/// The map-editing tools shared by the MCP server (and through it by ACP agents). Handlers run on a worker
/// thread and touch the map only inside <see cref="IMapHost.Invoke{T}"/>.
/// </summary>
public sealed partial class MapTools
{
    // Relaxed escaping keeps quotes/apostrophes/non-ASCII readable, which saves model tokens. The explicit resolver is needed
    // because JsonNode.ToJsonString marks the options read-only on newer runtimes and throws when none is set.
    internal static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public const string Instructions =
        "MT_Zenith (CEL map viewer/editor): inspect, build and verify Celeste .bin maps of the CEL (Desolo Zantas) mod. " +
        "Coordinates are pixels; entity/trigger x,y are relative to their room's top-left, rooms are placed in world pixels. " +
        "Tiles are 8x8: tile grids are rows of tileset chars ('0' = empty) and tile x,y are cell indices. A screen is 320x184 px (40x23 cells). " +
        "Room names are shown without the 'lvl_' prefix. Edits stay in memory until save_map (which also writes a .bak); use undo to revert the last edit. " +
        "BUILD LOOP for a complex map: design_notes (plan first, keep todo/log) -> build_room (a whole room in one call: placement by attach, size, fill, fg rows or draw ops, " +
        "exits, entities, spawn) -> connect_rooms (carve matching openings between touching rooms) -> analyze_room (movement-model check: which exits/items the player " +
        "reaches from the spawn, min tech per exit none/dash/advanced/unreachable, hazard pressure, routes, hints) -> fix with draw_tiles / entity tools -> analyze_map " +
        "detailed=true (openings connected/blocked/void, rooms reachable and traversable from the start) -> validate_map -> compare_to_tier -> save_map. " +
        "batch runs several tools as one undo step with rollback. render_room / analyze_room render=true show a room (and the reachability overlay). " +
        "Rooms come in size tiers normal/medium/large/xl/xxl (room_size_presets); resize_room grows or shrinks a room in any direction and can push neighbours. " +
        "PCG: pcg_generate (seleCte Markov sampler; candidates, repair, tier shaping, mechanics filter, per-room movement check, preview) -> pcg_import -> refine by hand; " +
        "pcg_train learns a new model from the open map or from reference maps of a difficulty tier. " +
        "Difficulty: tiers beginner, intermediate, advanced, expert, grandmaster. difficulty_profile gives densities/sizes/common entities; study a few example maps " +
        "with study_reference_map (installed collabs such as Strawberry Jam/Spring Collab), build original rooms, then check with compare_to_tier. " +
        "Reference maps belong to other creators: learn pacing, spacing and mechanic use from them, never reproduce their rooms. " +
        "game_* tools talk to a running Celeste with Everest debug mode (localhost:32270): game_teleport + game_screenshot to look at a saved room in-game. " +
        "Media: list_media finds the mod's images/audio/video; view_image shows sprites, GIFs, folders or animation frame sequences with pixel statistics; " +
        "compare_images diffs two images; analyze_audio and analyze_video return figures and statistics (and optionally the audio itself). " +
        "Paths are absolute or relative to the mod folder (also tried under Graphics/Atlases/Gameplay, Graphics, Audio). " +
        "Prompts: build_map (strongest-effort whole-level workflow), design_level, pcg_level, fix_room, review_difficulty, polish_map.";

    private readonly IMapHost _host;
    private readonly List<ToolDef> _tools = new();

    public MapTools(IMapHost host)
    {
        _host = host;
        Register();
    }

    public IReadOnlyList<ToolDef> All => _tools;

    public ToolResult Call(string name, JsonObject? args)
    {
        var tool = _tools.Find(t => t.Name == name);
        if (tool == null) return ToolResult.Text("Unknown tool: " + name, true);
        if (!tool.ReadOnly && _host.Invoke(() => _host.ReadOnly))
            return ToolResult.Text("The map viewer is in read-only mode (AI ▸ Read-only in the GUI, or --read-only); edits are blocked.", true);
        try
        {
            return tool.Run(args ?? new JsonObject());
        }
        catch (ToolException ex) { return ToolResult.Text(ex.Message, true); }
        catch (Exception ex) { return ToolResult.Text($"{ex.GetType().Name}: {ex.Message}", true); }
    }

    // ---- registry ----

    private void Add(string name, string description, bool readOnly, JsonObject props, string[] required, Func<JsonObject, ToolResult> run)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        _tools.Add(new ToolDef(name, description, schema, readOnly, run));
    }

    private static JsonObject P(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonObject Enum(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };

    private static JsonObject Props(params (string Key, JsonObject Schema)[] p)
    {
        var o = new JsonObject();
        foreach (var (k, s) in p) o[k] = s;
        return o;
    }

    private static readonly string[] None = Array.Empty<string>();
    private static JsonObject RoomP => P("string", "Room name (with or without the 'lvl_' prefix).");
    private static JsonObject LayerP => Enum("Tile layer: fg (solids) or bg.", "fg", "bg");
    private static JsonObject AttrsP => P("object", "Attribute values (string, number or boolean). Existing attributes keep their stored type.");
    private static JsonObject SizeP => Enum("Size tier: normal (1 screen), medium (2), large (4), xl (9), xxl (20 screens).", RoomSizing.Tiers.Select(t => t.Name).ToArray());
    private static JsonObject ShapeP => Enum("Shape for a size tier (default auto = keep the room's orientation).", RoomSizing.Shapes);

    private void Register()
    {
        // map
        Add("get_status", "Open map, unsaved state, room count, read-only mode and whether the game is reachable.", true, new(), None, _ => Status());
        Add("list_maps", "List the mod's map files (.bin) under Maps/, relative to the Maps folder.", true,
            Props(("filter", P("string", "Optional substring filter."))), None, ListMaps);
        Add("open_map", "Open a map file. Path is absolute or relative to the mod's Maps/ folder (e.g. 'CEL/Area/20_TheEnd.bin').", false,
            Props(("path", P("string", "Map path.")), ("discard", P("boolean", "Discard unsaved changes of the current map."))), new[] { "path" }, OpenMap);
        Add("save_map", "Save the open map (keeps the previous file as .bin.bak). Optional path saves a copy elsewhere.", false,
            Props(("path", P("string", "Optional target path."))), None, SaveMap);
        Add("undo", "Undo the last edit.", false, new(), None, _ => Edit0(() => _host.Undo() ? "Undone." : "Nothing to undo."));
        Add("validate_map", "Check the open map for problems: duplicate ids/room names, overlapping rooms, rooms without a player spawn, entities outside their room, sizes not multiple of 8, unknown entity names.", true,
            new(), None, _ => Validate());

        // rooms
        Add("list_rooms", "List rooms with position, size, entity/trigger counts and whether they have a player spawn.", true,
            Props(("filter", P("string", "Optional substring filter on room names."))), None, ListRooms);
        Add("get_room", "Room attributes, entities and triggers (with attributes and nodes) and optionally the fg/bg tile grids.", true,
            Props(("room", RoomP), ("include_tiles", P("boolean", "Include fg/bg tile rows (default true)."))), new[] { "room" }, GetRoom);
        Add("render_room", "Render a room (tiles, decals, entity/trigger boxes) to a PNG image.", true,
            Props(("room", RoomP), ("scale", P("number", "Pixel scale (default: fit to 1600px, max 2).")),
                  ("show_entities", P("boolean", "Draw entity and trigger boxes with labels (default true)."))), new[] { "room" }, RenderRoom);
        Add("add_room", "Add an empty room. Without x/y it is placed right of the map. Size it with width/height or a size tier (normal … xxl, see room_size_presets).", false,
            Props(("name", P("string", "Room name.")), ("x", P("integer", "World x (multiple of 8).")), ("y", P("integer", "World y (multiple of 8).")),
                  ("width", P("integer", "Width in pixels (default 320, or from size).")), ("height", P("integer", "Height in pixels (default 184, or from size).")),
                  ("size", SizeP), ("shape", ShapeP),
                  ("attrs", AttrsP)), new[] { "name" }, AddRoom);
        Add("room_size_presets", "Room size tiers (normal, medium, large, xl, xxl) with pixel sizes per shape (wide/square/tall), the anchors and fill modes " +
            "resize_room accepts, and how the open map's rooms spread over the tiers. Tiers follow vanilla/CEL A-Side/Strawberry Jam/Spring Collab room sizes.", true,
            new(), None, _ => SizePresets());
        Add("resize_room", "Resize or enlarge a room in any direction, from a normal one-screen room up to xxl, keeping its content fixed in the world. " +
            "Pick the target with size (+shape) / width+height / screens_w+screens_h / scale, held at an anchor (the room grows away from it; 'center' grows all ways); " +
            "or move edges directly with left/top/right/bottom (+grow, -shrink) or direction+amount (e.g. direction='left,up' or 'all'). " +
            "New space is filled by extending the edge tiles by default so floors/ceilings/walls stay closed. push_rooms shifts the rooms beyond a growing edge so nothing overlaps.", false,
            Props(("room", RoomP),
                  ("size", SizeP), ("shape", ShapeP),
                  ("width", P("integer", "Target width in pixels.")), ("height", P("integer", "Target height in pixels.")),
                  ("screens_w", P("number", "Target width in screens (320px).")), ("screens_h", P("number", "Target height in screens (184px).")),
                  ("scale", P("number", "Multiply the current size (e.g. 2 doubles width and height).")),
                  ("anchor", Enum("Part of the room that stays in place for size/width/height/screens/scale targets (default top-left).", RoomSizing.Anchors)),
                  ("left", P("integer", "Move the left edge outward by this many pixels (negative shrinks).")),
                  ("top", P("integer", "Move the top edge outward (negative shrinks).")),
                  ("right", P("integer", "Move the right edge outward (negative shrinks).")),
                  ("bottom", P("integer", "Move the bottom edge outward (negative shrinks).")),
                  ("direction", P("string", "Edges to grow by 'amount': comma list of left, right, up/top, down/bottom, horizontal, vertical, all.")),
                  ("amount", P("integer", "Pixels per edge for 'direction' (negative shrinks). Default one screen (320 horizontally, 184 vertically).")),
                  ("amount_screens", P("number", "Like amount, in screens.")),
                  ("fill", P("string", "New space: 'extend' (default, continue edge tiles), 'empty', 'solid', or a tileset char.")),
                  ("push_rooms", P("boolean", "Shift rooms beyond each growing edge by the same amount (default false)."))),
            new[] { "room" }, ResizeRoomTool);
        Add("update_room", "Move/resize a room (content stays in place in the world), rename it or set room attributes (music, dark, windPattern, ...).", false,
            Props(("room", RoomP), ("x", P("integer", "New world x.")), ("y", P("integer", "New world y.")),
                  ("width", P("integer", "New width.")), ("height", P("integer", "New height.")),
                  ("rename", P("string", "New room name.")), ("attrs", AttrsP)), new[] { "room" }, UpdateRoom);
        Add("delete_room", "Delete a room.", false, Props(("room", RoomP)), new[] { "room" }, DeleteRoom);

        // tiles
        Add("list_tilesets", "Tileset chars and names for the fg or bg layer of the open map.", true,
            Props(("layer", LayerP)), None, ListTilesets);
        Add("set_tiles", "Write tile chars into a room starting at cell (x,y). Each row string covers consecutive cells; a space leaves a cell unchanged, '0' clears it.", false,
            Props(("room", RoomP), ("layer", LayerP), ("x", P("integer", "Start column.")), ("y", P("integer", "Start row.")),
                  ("rows", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Tile rows." })),
            new[] { "room", "rows" }, SetTiles);
        Add("fill_rect", "Fill a rectangle of cells with one tile char ('0' clears).", false,
            Props(("room", RoomP), ("layer", LayerP), ("x", P("integer", "Column.")), ("y", P("integer", "Row.")),
                  ("width", P("integer", "Columns.")), ("height", P("integer", "Rows.")), ("tile", P("string", "Tileset char."))),
            new[] { "room", "x", "y", "width", "height", "tile" }, FillRect);

        // entities
        Add("list_placements", "Search the entity/trigger types known from installed Lönn plugins (name, source mod, default attributes).", true,
            Props(("query", P("string", "Substring of the type or placement name.")), ("kind", Enum("Filter.", "all", "entity", "trigger")),
                  ("limit", P("integer", "Max results (default 40)."))), None, ListPlacements);
        Add("add_entity", "Place an entity (or trigger) in a room. Defaults come from the Lönn placement when the type is known.", false,
            Props(("room", RoomP), ("name", P("string", "Entity type, e.g. 'spikesUp', 'refill', 'CEL/...'.")),
                  ("x", P("number", "Room-local x.")), ("y", P("number", "Room-local y.")),
                  ("trigger", P("boolean", "Place as a trigger.")), ("placement", P("string", "Placement name to take defaults from.")),
                  ("attrs", AttrsP), ("nodes", P("array", "Nodes as [[x,y], ...] (room-local)."))),
            new[] { "room", "name", "x", "y" }, AddEntity);
        Add("update_entity", "Change an entity's or trigger's position, attributes or nodes (by id).", false,
            Props(("room", RoomP), ("id", P("integer", "Entity id.")), ("x", P("number", "Room-local x.")), ("y", P("number", "Room-local y.")),
                  ("attrs", AttrsP), ("remove_attrs", P("array", "Attribute names to remove.")), ("nodes", P("array", "Replace nodes: [[x,y], ...]."))),
            new[] { "room", "id" }, UpdateEntity);
        Add("remove_entity", "Remove an entity or trigger by id.", false,
            Props(("room", RoomP), ("id", P("integer", "Entity id."))), new[] { "room", "id" }, RemoveEntity);
        Add("search_entities", "Find entities/triggers across the map by type name and/or attribute value.", true,
            Props(("name", P("string", "Substring of the type name.")), ("attr", P("string", "Attribute name that must exist.")),
                  ("value", P("string", "Substring the attribute value must contain.")), ("room", P("string", "Limit to one room.")),
                  ("limit", P("integer", "Max results (default 100)."))), None, SearchEntities);

        // game
        Add("game_state", "The running game's level, room, player position (needs Celeste with Everest debug mode).", true, new(), None, _ => GameState());
        Add("game_teleport", "Load a room of the open (saved) map in the running game, optionally at a room-local position.", true,
            Props(("room", RoomP), ("x", P("number", "Room-local x.")), ("y", P("number", "Room-local y.")),
                  ("exact", P("boolean", "Use the exact point instead of the nearest spawn."))), new[] { "room" }, Teleport);
        Add("game_reload", "Reload the saved map in the running game.", true, new(), None, _ => GameReload());
        Add("read_game_log", "Tail Celeste's log.txt (exceptions, Everest messages).", true,
            Props(("lines", P("integer", "Lines from the end (default 80).")), ("filter", P("string", "Only lines containing this text."))), None, ReadLog);

        RegisterDesign();
        RegisterPcg();
        RegisterReference();
        RegisterMedia();
    }

    // ---- args ----

    private static string? S(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue(out string? s) ? s : a[k]?.ToString();

    private static string Req(JsonObject a, string k) =>
        S(a, k) is { Length: > 0 } s ? s : throw new ToolException($"Missing required argument '{k}'.");

    private static double? D(JsonObject a, string k)
    {
        if (a[k] is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    private static int? I(JsonObject a, string k) => D(a, k) is double d ? (int)Math.Round(d) : null;

    private static bool B(JsonObject a, string k, bool def = false)
    {
        if (a[k] is not JsonValue v) return def;
        if (v.TryGetValue(out bool b)) return b;
        return v.TryGetValue(out string? s) && bool.TryParse(s, out b) ? b : def;
    }

    private static bool Fg(JsonObject a) => !string.Equals(S(a, "layer"), "bg", StringComparison.OrdinalIgnoreCase);

    /// <summary>A JSON value as a map attribute value, using the attribute's current type as a hint.</summary>
    private static object ToValue(JsonNode? n, BinType? hint)
    {
        if (n is not JsonValue v) return n?.ToJsonString() ?? "";
        switch (v.GetValueKind())
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number:
                double d = v.GetValue<double>();
                if (hint != BinType.Float && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
                return (float)d;
            default:
                string s = v.ToString();
                return hint == null ? s : MapBin.ParseText(s, hint);
        }
    }

    private static JsonNode? FromValue(object? v) => v switch
    {
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        float f => JsonValue.Create(f),
        null => null,
        _ => JsonValue.Create(v.ToString())
    };

    private static void ApplyAttrs(BinElement el, JsonObject a, params string[] protectedKeys)
    {
        if (a["attrs"] is not JsonObject attrs) return;
        foreach (var (k, v) in attrs)
        {
            if (protectedKeys.Contains(k)) throw new ToolException($"Attribute '{k}' can't be set here; use the dedicated argument.");
            if (k == "innerText") throw new ToolException("Tile data can't be set as an attribute; use set_tiles.");
            el.Set(k, ToValue(v, el.Find(k)?.Type));
        }
    }

    private static List<(float X, float Y)> Nodes(JsonObject a)
    {
        var list = new List<(float, float)>();
        if (a["nodes"] is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            if (n is JsonArray p && p.Count >= 2) list.Add(((float)p[0]!.GetValue<double>(), (float)p[1]!.GetValue<double>()));
            else if (n is JsonObject o && D(o, "x") is double x && D(o, "y") is double y) list.Add(((float)x, (float)y));
            else throw new ToolException("Nodes must be [[x,y], ...].");
        }
        return list;
    }

    // ---- map access ----

    private MapBin MapOrThrow() => _host.Map ?? throw new ToolException("No map is open. Use list_maps and open_map first.");

    private Room FindRoom(string name)
    {
        var map = MapOrThrow();
        if (name.StartsWith("lvl_")) name = name[4..];
        var levels = MapModel.Levels(map).Children.Select(l => new Room(l)).ToList();
        return levels.FirstOrDefault(r => r.Name == name)
            ?? levels.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException($"No room '{name}'. Use list_rooms.");
    }

    private static (BinElement El, bool Trigger) FindEntity(Room room, int id)
    {
        foreach (var e in room.EntitiesEl.Children) if (e.Int("id") == id) return (e, false);
        foreach (var e in room.TriggersEl.Children) if (e.Int("id") == id) return (e, true);
        throw new ToolException($"No entity or trigger with id {id} in room '{room.Name}'.");
    }

    /// <summary>Runs an edit on the map thread with undo + refresh (inside a batch the batch owns the undo step).</summary>
    private ToolResult Edit(Func<(BinElement? Level, string Summary)> f) => _host.Invoke(() =>
    {
        MapOrThrow();
        if (_batchDepth == 0) _host.BeginEdit();
        BinElement? level;
        string summary;
        try { (level, summary) = f(); }
        catch when (_batchDepth == 0 && _host.Undo())
        {
            // a failed edit must not leave half of its changes behind: restore the snapshot, then report the error
            _host.EndEdit(null, "edit failed");
            throw;
        }
        _host.EndEdit(level, summary);
        _host.Log("[AI] " + summary);
        return ToolResult.Text(summary);
    });

    private ToolResult Edit0(Func<string> f) => _host.Invoke(() =>
    {
        string s = f();
        _host.Log("[AI] " + s);
        return ToolResult.Text(s);
    });

    internal static JsonObject EntityJson(BinElement e, bool trigger)
    {
        var o = new JsonObject { ["id"] = e.Int("id"), ["name"] = e.Name, ["x"] = e.Num("x"), ["y"] = e.Num("y") };
        if (trigger) o["trigger"] = true;
        var attrs = new JsonObject();
        foreach (var a in e.Attrs)
            if (a.Key is not ("id" or "x" or "y" or "originX" or "originY")) attrs[a.Key] = FromValue(a.Value);
        if (attrs.Count > 0) o["attrs"] = attrs;
        var nodes = e.Children.Where(c => c.Name == "node").Select(n => (JsonNode)new JsonArray(n.Num("x"), n.Num("y"))).ToArray();
        if (nodes.Length > 0) o["nodes"] = new JsonArray(nodes);
        return o;
    }

    private static string? ModDir(string? mapPath) => (mapPath != null ? MapModel.ModDirOf(mapPath) : null) ?? GameAssets.ModDir;

    // ---- map tools ----

    private ToolResult Status()
    {
        var o = _host.Invoke(() => new JsonObject
        {
            ["map"] = _host.Path,
            ["dirty"] = _host.Dirty,
            ["readOnly"] = _host.ReadOnly,
            ["rooms"] = _host.Map == null ? 0 : MapModel.Levels(_host.Map).Children.Count,
            ["fgTileset"] = _host.FgTiler?.Source,
            ["modDir"] = ModDir(_host.Path)
        });
        var st = _host.Game.GetStateAsync().GetAwaiter().GetResult();
        o["game"] = st == null ? "not running (or Everest debug RC off)" : st.InLevel ? $"{st.Sid} {st.Side} room {st.Room}" : "running, not in a level";
        return ToolResult.Json(o);
    }

    private ToolResult ListMaps(JsonObject a)
    {
        string? mod = _host.Invoke(() => ModDir(_host.Path));
        string maps = mod == null ? "" : System.IO.Path.Combine(mod, "Maps");
        if (!Directory.Exists(maps)) throw new ToolException("Maps folder not found.");
        string f = S(a, "filter") ?? "";
        var list = Directory.EnumerateFiles(maps, "*.bin", SearchOption.AllDirectories)
            .Select(p => System.IO.Path.GetRelativePath(maps, p).Replace('\\', '/'))
            .Where(p => f.Length == 0 || p.Contains(f, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        return ToolResult.Text($"{list.Count} maps under {maps}:\n" + string.Join("\n", list));
    }

    private string ResolveMap(string p)
    {
        if (System.IO.Path.IsPathRooted(p)) return p;
        string? mod = ModDir(_host.Path);
        var candidates = new List<string>();
        foreach (string q in p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ? new[] { p } : new[] { p, p + ".bin" })
        {
            if (mod != null)
            {
                candidates.Add(System.IO.Path.Combine(mod, "Maps", q));
                candidates.Add(System.IO.Path.Combine(mod, q));
                candidates.Add(System.IO.Path.Combine(mod, "Maps", "CEL", q));
            }
            candidates.Add(System.IO.Path.GetFullPath(q));
        }
        return candidates.FirstOrDefault(File.Exists) ?? throw new ToolException($"Map not found: {p}. Use list_maps.");
    }

    private ToolResult OpenMap(JsonObject a)
    {
        string path = ResolveMap(Req(a, "path"));
        return _host.Invoke(() =>
        {
            if (_host.Dirty && !B(a, "discard"))
                throw new ToolException("The open map has unsaved changes. Save it, or pass discard=true.");
            _host.Open(path);
            _host.Log("[AI] Opened " + path);
            return ToolResult.Text($"Opened {path} ({MapModel.Levels(_host.Map!).Children.Count} rooms).");
        });
    }

    private ToolResult SaveMap(JsonObject a) => _host.Invoke(() =>
    {
        MapOrThrow();
        string path = S(a, "path") is { Length: > 0 } p ? System.IO.Path.GetFullPath(p) : _host.Path ?? throw new ToolException("No path.");
        _host.Save(path);
        _host.Log("[AI] Saved " + path);
        return ToolResult.Text($"Saved {path} (previous version kept as .bin.bak).");
    });

    private ToolResult Validate() => _host.Invoke(() =>
    {
        var map = MapOrThrow();
        var issues = new List<string>();
        var rooms = MapModel.Levels(map).Children.Select(l => new Room(l)).ToList();

        foreach (var g in rooms.GroupBy(r => r.Name).Where(g => g.Count() > 1))
            issues.Add($"Duplicate room name '{g.Key}' ({g.Count()}x).");
        var ids = new Dictionary<int, string>();
        var unknown = new Dictionary<string, int>();
        bool catalog = !PluginCatalog.Loading && PluginCatalog.FileCount > 0;
        foreach (var r in rooms)
        {
            var outside = new List<string>();
            if (r.X % 8 != 0 || r.Y % 8 != 0 || r.W % 8 != 0 || r.H % 8 != 0)
                issues.Add($"{r.Name}: position/size not a multiple of 8 ({r.X},{r.Y} {r.W}x{r.H}).");
            if (!r.EntitiesEl.Children.Any(MapModel.IsSpawn))
                issues.Add($"{r.Name}: no player spawn.");
            foreach (var (list, trig) in new[] { (r.EntitiesEl, false), (r.TriggersEl, true) })
                foreach (var e in list.Children)
                {
                    int id = e.Int("id");
                    if (ids.TryGetValue(id, out string? other)) issues.Add($"Duplicate id {id}: {e.Name} in {r.Name} and {other}.");
                    else ids[id] = $"{e.Name} in {r.Name}";
                    float x = e.Num("x"), y = e.Num("y");
                    if (x < -8 || y < -8 || x > r.W + 8 || y > r.H + 8)
                        outside.Add($"{e.Name} #{id} at ({x},{y})");
                    if (catalog && (trig ? PluginCatalog.Trigger(e.Name) : PluginCatalog.Entity(e.Name)) == null)
                        unknown[e.Name] = unknown.GetValueOrDefault(e.Name) + 1;
                }
            if (outside.Count > 0)
                issues.Add($"{r.Name} ({r.W}x{r.H}): {outside.Count} placed outside the room, e.g. {string.Join(", ", outside.Take(3))}.");
        }
        for (int i = 0; i < rooms.Count; i++)
            for (int j = i + 1; j < rooms.Count; j++)
                if (rooms[i].Bounds.IntersectsWith(rooms[j].Bounds))
                    issues.Add($"Rooms {rooms[i].Name} and {rooms[j].Name} overlap.");
        foreach (var (n, c) in unknown.OrderByDescending(kv => kv.Value).Take(30))
            issues.Add($"Unknown type '{n}' ({c}x) — no installed Lönn plugin defines it.");
        if (rooms.Count > 1)
        {
            var graph = MapGraph.Analyze(map, detailed: false);
            issues.AddRange(graph.Issues.Where(i => !i.Contains("no player spawn")).Select(i => "Connectivity: " + i));
        }

        var sb = new StringBuilder($"{rooms.Count} rooms, {ids.Count} entities/triggers: ");
        sb.Append(issues.Count == 0 ? "no problems found." : $"{issues.Count} issue(s):\n" + string.Join("\n", issues.Take(200)) + (issues.Count > 200 ? $"\n… and {issues.Count - 200} more." : ""));
        if (!catalog) sb.Append("\n(Lönn plugin catalog not loaded yet; unknown-type check skipped.)");
        return ToolResult.Text(sb.ToString());
    });

    // ---- room tools ----

    private ToolResult ListRooms(JsonObject a) => _host.Invoke(() =>
    {
        string f = S(a, "filter") ?? "";
        var arr = new JsonArray();
        foreach (var r in MapModel.Levels(MapOrThrow()).Children.Select(l => new Room(l)))
            if (f.Length == 0 || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
                arr.Add(new JsonObject
                {
                    ["name"] = r.Name, ["x"] = r.X, ["y"] = r.Y, ["width"] = r.W, ["height"] = r.H, ["size"] = RoomSizing.TierOf(r.W, r.H),
                    ["entities"] = r.EntitiesEl.Children.Count, ["triggers"] = r.TriggersEl.Children.Count,
                    ["spawn"] = r.EntitiesEl.Children.Any(MapModel.IsSpawn)
                });
        return ToolResult.Json(arr);
    });

    private ToolResult GetRoom(JsonObject a) => _host.Invoke(() =>
    {
        var r = FindRoom(Req(a, "room"));
        var attrs = new JsonObject();
        foreach (var at in r.El.Attrs) attrs[at.Key] = FromValue(at.Value);
        var o = new JsonObject
        {
            ["name"] = r.Name, ["cols"] = r.Cols, ["rows"] = r.Rows, ["attrs"] = attrs,
            ["entities"] = new JsonArray(r.EntitiesEl.Children.Select(e => (JsonNode)EntityJson(e, false)).ToArray()),
            ["triggers"] = new JsonArray(r.TriggersEl.Children.Select(e => (JsonNode)EntityJson(e, true)).ToArray()),
            ["fgDecals"] = r.El.Child("fgdecals")?.Children.Count ?? 0,
            ["bgDecals"] = r.El.Child("bgdecals")?.Children.Count ?? 0
        };
        if (B(a, "include_tiles", true))
        {
            o["fg"] = GridRows(r.Grid(true));
            o["bg"] = GridRows(r.Grid(false));
        }
        return ToolResult.Json(o);
    });

    internal static JsonArray GridRows(TileGrid g)
    {
        var arr = new JsonArray();
        var sb = new StringBuilder(g.Cols);
        for (int y = 0; y < g.Rows; y++)
        {
            sb.Clear();
            for (int x = 0; x < g.Cols; x++) sb.Append(g[x, y]);
            arr.Add(sb.ToString());
        }
        return arr;
    }

    private ToolResult RenderRoom(JsonObject a)
    {
        bool showEntities = B(a, "show_entities", true);
        var (job, boxes, name) = _host.Invoke(() =>
        {
            var r = FindRoom(Req(a, "room"));
            var b = showEntities ? EntityBoxes(r) : new List<(RectangleF Box, string Label, int Kind)>();
            return (RoomRenderJob.From(r, _host.FgTiler, _host.BgTiler), b, r.Name);
        });
        return RenderPng(job, boxes, D(a, "scale"), $"Room {name}");
    }

    internal static List<(RectangleF Box, string Label, int Kind)> EntityBoxes(Room r)
    {
        var b = new List<(RectangleF Box, string Label, int Kind)>();
        foreach (var e in r.TriggersEl.Children) b.Add((Local(r, MapModel.Box(r, e, true)), e.Name, 2));
        foreach (var e in r.EntitiesEl.Children) b.Add((Local(r, MapModel.Box(r, e, false)), e.Name, MapModel.IsSpawn(e) ? 1 : 0));
        return b;
    }

    /// <summary>Renders a room job plus labelled entity boxes to a PNG tool result.</summary>
    internal static ToolResult RenderPng(RoomRenderJob job, List<(RectangleF Box, string Label, int Kind)> boxes, double? requestedScale, string title,
                                         Action<Graphics, float>? overlay = null)
    {
        using var img = job.Render();
        float scale = requestedScale is double s && s > 0 ? (float)Math.Min(s, 4) : Math.Min(2f, 1600f / Math.Max(job.W, job.H));
        int ow = Math.Max(1, (int)(job.W * scale)), oh = Math.Max(1, (int)(job.H * scale));
        if ((long)ow * oh > 16_000_000) throw new ToolException("Image too large; pass a smaller scale.");
        using var bmp = new Bitmap(ow, oh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(24, 24, 32));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (var layer in new[] { RoomImage.Level(img.Bg, 0), RoomImage.Level(img.Fg, 0) })
                if (layer != null) g.DrawImage(layer, 0, 0, ow, oh);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.SmoothingMode = SmoothingMode.None;
            overlay?.Invoke(g, scale);
            using var font = new Font("Segoe UI", Math.Clamp(7f * scale, 7f, 11f));
            foreach (var (box, label, kind) in boxes)
            {
                var rect = new RectangleF(box.X * scale, box.Y * scale, Math.Max(2, box.Width * scale), Math.Max(2, box.Height * scale));
                Color c = kind switch { 1 => Color.LimeGreen, 2 => Color.Orange, _ => Color.FromArgb(255, 90, 90) };
                using (var fill = new SolidBrush(Color.FromArgb(kind == 2 ? 40 : 60, c))) g.FillRectangle(fill, rect);
                using (var pen = new Pen(c)) g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                using var tb = new SolidBrush(Color.White);
                g.DrawString(label, font, Brushes.Black, rect.X + 1, rect.Y + 1);
                g.DrawString(label, font, tb, rect.X, rect.Y);
            }
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new ToolResult()
            .AddText($"{title}: {job.W}x{job.H}px rendered at scale {scale:0.##} ({ow}x{oh}). Red = entities, green = player spawn, orange = triggers.")
            .AddPng(ms.ToArray());
    }

    internal static RectangleF Local(Room r, RectangleF world) => new(world.X - r.X, world.Y - r.Y, world.Width, world.Height);

    private ToolResult AddRoom(JsonObject a) => Edit(() =>
    {
        var map = MapOrThrow();
        var preset = S(a, "size") is { Length: > 0 } tier ? Arg(() => RoomSizing.TierSize(tier, S(a, "shape") ?? "auto")) : new Size(320, 184);
        int w = Math.Max(8, MapModel.Snap8(I(a, "width") ?? preset.Width)), h = Math.Max(8, MapModel.Snap8(I(a, "height") ?? preset.Height));
        int x, y;
        if (I(a, "x") is int ax && I(a, "y") is int ay) { x = MapModel.Snap8(ax); y = MapModel.Snap8(ay); }
        else
        {
            var b = MapModel.MapBounds(map);
            x = MapModel.Snap8(b.Right + 16); y = MapModel.Snap8(b.Top);
        }
        string name = MapModel.UniqueName(map, Req(a, "name"));
        var level = MapModel.NewLevel(name, x, y, w, h);
        ApplyAttrs(level, a, "name", "x", "y", "width", "height");
        MapModel.Levels(map).Children.Add(level);
        var nr = new Room(level);
        string warn = MapModel.Levels(map).Children.Where(l => l != level).Select(l => new Room(l)).FirstOrDefault(r => r.Bounds.IntersectsWith(nr.Bounds)) is { } o
            ? $" Warning: overlaps room {o.Name}." : "";
        return (null, $"Added room {nr.Name} at ({x},{y}) {w}x{h}. It has no player spawn yet (add_entity name='player').{warn}");
    });

    private ToolResult UpdateRoom(JsonObject a) => Edit(() =>
    {
        var map = MapOrThrow();
        var r = FindRoom(Req(a, "room"));
        var changes = new List<string>();
        var nb = new Rectangle(I(a, "x") ?? r.X, I(a, "y") ?? r.Y, I(a, "width") ?? r.W, I(a, "height") ?? r.H);
        nb = new Rectangle(MapModel.Snap8(nb.X), MapModel.Snap8(nb.Y), Math.Max(8, MapModel.Snap8(nb.Width)), Math.Max(8, MapModel.Snap8(nb.Height)));
        bool structural = false;
        if (nb != r.Bounds)
        {
            MapModel.ResizeRoom(r, nb);
            changes.Add($"bounds → ({nb.X},{nb.Y}) {nb.Width}x{nb.Height}");
            structural = true;
        }
        if (S(a, "rename") is { Length: > 0 } rename)
        {
            string bare = rename.StartsWith("lvl_") ? rename[4..] : rename;
            if (MapModel.Levels(map).Children.Any(l => l != r.El && new Room(l).Name == bare))
                throw new ToolException($"A room named '{bare}' already exists.");
            r.El.Set("name", (r.RawName.StartsWith("lvl_") ? "lvl_" : "") + bare);
            changes.Add("renamed to " + bare);
            structural = true;
        }
        if (a["attrs"] is JsonObject attrs && attrs.Count > 0)
        {
            ApplyAttrs(r.El, a, "name", "x", "y", "width", "height");
            changes.Add("attrs " + string.Join(", ", attrs.Select(kv => kv.Key)));
        }
        if (changes.Count == 0) throw new ToolException("Nothing to change.");
        return (structural ? null : r.El, $"Room {r.Name}: {string.Join("; ", changes)}.");
    });

    private static T Arg<T>(Func<T> f)
    {
        try { return f(); }
        catch (ArgumentException ex) { throw new ToolException(ex.Message); }
    }

    private ToolResult SizePresets() => _host.Invoke(() =>
    {
        var tiers = new JsonArray();
        foreach (var (name, screens, use) in RoomSizing.Tiers)
        {
            var shapes = new JsonObject();
            foreach (string s in new[] { "wide", "square", "tall" })
            {
                var sz = RoomSizing.TierSize(name, s);
                shapes[s] = $"{sz.Width}x{sz.Height}";
            }
            tiers.Add(new JsonObject { ["size"] = name, ["screens"] = screens, ["use"] = use, ["shapes"] = shapes });
        }
        var o = new JsonObject
        {
            ["screen"] = $"{RoomSizing.ScreenW}x{RoomSizing.ScreenH}",
            ["tiers"] = tiers,
            ["anchors"] = new JsonArray(RoomSizing.Anchors.Select(x => (JsonNode)x).ToArray()),
            ["fill"] = "extend (default) | empty | solid | <tileset char>",
            ["reference"] = "Room sizes in CEL A-Sides, Strawberry Jam and Spring Collab: median ~400x200, 75th percentile ~900x420; " +
                            "~30% normal, ~15% medium, ~20% large, ~15% xl, ~15% xxl or larger (corridors up to 32000px wide, climbs up to 8000px tall).",
        };
        if (_host.Map is { } map)
        {
            var counts = new JsonObject();
            foreach (var g in MapModel.Levels(map).Children.Select(l => new Room(l)).GroupBy(r => RoomSizing.TierOf(r.W, r.H)))
                counts[g.Key] = g.Count();
            o["open_map"] = counts;
        }
        return ToolResult.Json(o);
    });

    private ToolResult ResizeRoomTool(JsonObject a) => Edit(() =>
    {
        var map = MapOrThrow();
        var r = FindRoom(Req(a, "room"));
        var b = r.Bounds;
        int l = I(a, "left") ?? 0, t = I(a, "top") ?? 0, rt = I(a, "right") ?? 0, bt = I(a, "bottom") ?? 0;

        if (S(a, "direction") is { Length: > 0 } dir)
        {
            int? amount = I(a, "amount") ?? (D(a, "amount_screens") is double sc ? (int)Math.Round(sc * RoomSizing.ScreenW) : null);
            int? amountV = I(a, "amount") ?? (D(a, "amount_screens") is double sv ? (int)Math.Round(sv * RoomSizing.ScreenH) : null);
            int h = amount ?? RoomSizing.ScreenW, v = amountV ?? RoomSizing.ScreenH;
            foreach (string d in dir.ToLowerInvariant().Split(new[] { ',', ' ', '+', '|' }, StringSplitOptions.RemoveEmptyEntries))
                switch (d)
                {
                    case "left" or "west" or "w": l += h; break;
                    case "right" or "east" or "e": rt += h; break;
                    case "up" or "top" or "north" or "n": t += v; break;
                    case "down" or "bottom" or "south" or "s": bt += v; break;
                    case "horizontal" or "x": l += h; rt += h; break;
                    case "vertical" or "y": t += v; bt += v; break;
                    case "all" or "any" or "every": l += h; rt += h; t += v; bt += v; break;
                    default: throw new ToolException($"Unknown direction '{d}'. Use left, right, up, down, horizontal, vertical or all.");
                }
        }

        Size? target = null;
        if (S(a, "size") is { Length: > 0 } tier) target = Arg(() => RoomSizing.TierSize(tier, S(a, "shape") ?? "auto", b.Size));
        if (D(a, "scale") is double k)
        {
            if (k <= 0) throw new ToolException("scale must be positive.");
            target = new Size((int)Math.Round(b.Width * k), (int)Math.Round(b.Height * k));
        }
        if (D(a, "screens_w") is double sw2) target = new Size((int)Math.Round(sw2 * RoomSizing.ScreenW), target?.Height ?? b.Height);
        if (D(a, "screens_h") is double sh2) target = new Size(target?.Width ?? b.Width, (int)Math.Round(sh2 * RoomSizing.ScreenH));
        if (I(a, "width") is int tw) target = new Size(tw, target?.Height ?? b.Height);
        if (I(a, "height") is int th) target = new Size(target?.Width ?? b.Width, th);
        if (target is { } ts)
        {
            var (dl, dt, dr, db) = Arg(() => RoomSizing.DeltasFor(b, ts, S(a, "anchor") ?? "top-left"));
            l += dl; t += dt; rt += dr; bt += db;
        }
        if (l == 0 && t == 0 && rt == 0 && bt == 0)
            throw new ToolException("Nothing to change: give size, width/height, screens_w/screens_h, scale, left/top/right/bottom or direction.");

        var (fill, tile) = Arg(() => RoomSizing.ParseFill(S(a, "fill")));
        var res = Arg(() => RoomSizing.Resize(map, r, l, t, rt, bt, fill, tile, B(a, "push_rooms")));
        var sb = new StringBuilder($"Room {r.Name}: ({res.Old.X},{res.Old.Y}) {res.Old.Width}x{res.Old.Height} [{RoomSizing.TierOf(res.Old.Width, res.Old.Height)}] → " +
                                   $"({res.New.X},{res.New.Y}) {res.New.Width}x{res.New.Height} [{RoomSizing.TierOf(res.New.Width, res.New.Height)}]; " +
                                   $"edges left {l:+0;-0;0}, top {t:+0;-0;0}, right {rt:+0;-0;0}, bottom {bt:+0;-0;0}; fill {fill.ToString().ToLowerInvariant()}.");
        if (res.Pushed.Count > 0) sb.Append($" Pushed {res.Pushed.Count} room(s): {string.Join(", ", res.Pushed.Take(20))}{(res.Pushed.Count > 20 ? ", …" : "")}.");
        if (res.Overlaps.Count > 0) sb.Append($" Warning: now overlaps {string.Join(", ", res.Overlaps.Take(10))} (move them, or redo with push_rooms=true).");
        return (null, sb.ToString());
    });

    private ToolResult DeleteRoom(JsonObject a) => Edit(() =>
    {
        var r = FindRoom(Req(a, "room"));
        MapModel.Levels(MapOrThrow()).Children.Remove(r.El);
        return (null, $"Deleted room {r.Name}.");
    });

    // ---- tile tools ----

    private ToolResult ListTilesets(JsonObject a) => _host.Invoke(() =>
    {
        MapOrThrow();
        bool fg = Fg(a);
        var tiler = fg ? _host.FgTiler : _host.BgTiler;
        if (tiler == null) return ToolResult.Text("No tileset xml could be loaded for this layer.", true);
        var lines = tiler.Tilesets.OrderBy(t => t.Id).Select(t => $"{t.Id}  {t.DisplayName}  ({t.Path})");
        return ToolResult.Text($"{(fg ? "fg" : "bg")} tilesets from {tiler.Source} ('0' = empty):\n" + string.Join("\n", lines));
    });

    private ToolResult SetTiles(JsonObject a) => Edit(() =>
    {
        var r = FindRoom(Req(a, "room"));
        bool fg = Fg(a);
        if (a["rows"] is not JsonArray rows) throw new ToolException("'rows' must be an array of strings.");
        int x0 = I(a, "x") ?? 0, y0 = I(a, "y") ?? 0, n = 0, clipped = 0;
        var grid = r.Grid(fg);
        var tiler = fg ? _host.FgTiler : _host.BgTiler;
        var unknown = new HashSet<char>();
        for (int j = 0; j < rows.Count; j++)
        {
            string row = rows[j]?.ToString() ?? "";
            for (int i = 0; i < row.Length; i++)
            {
                char c = row[i];
                if (c == ' ') continue;
                int x = x0 + i, y = y0 + j;
                if ((uint)x >= (uint)grid.Cols || (uint)y >= (uint)grid.Rows) { clipped++; continue; }
                if (c != '0' && tiler != null && !tiler.Has(c)) unknown.Add(c);
                grid[x, y] = c;
                n++;
            }
        }
        r.CommitGrid(fg);
        string msg = $"Set {n} {(fg ? "fg" : "bg")} tiles in {r.Name}.";
        if (clipped > 0) msg += $" {clipped} outside the room ({grid.Cols}x{grid.Rows} cells) were skipped.";
        if (unknown.Count > 0) msg += $" Warning: unknown tileset chars {string.Join("", unknown)} (see list_tilesets).";
        return (r.El, msg);
    });

    private ToolResult FillRect(JsonObject a) => Edit(() =>
    {
        var r = FindRoom(Req(a, "room"));
        bool fg = Fg(a);
        string tile = Req(a, "tile");
        if (tile.Length != 1) throw new ToolException("'tile' must be a single tileset char.");
        int x0 = I(a, "x") ?? 0, y0 = I(a, "y") ?? 0, w = I(a, "width") ?? 1, h = I(a, "height") ?? 1;
        var grid = r.Grid(fg);
        int n = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(grid.Rows, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(grid.Cols, x0 + w); x++) { grid[x, y] = tile[0]; n++; }
        r.CommitGrid(fg);
        return (r.El, $"Filled {n} {(fg ? "fg" : "bg")} cells with '{tile}' in {r.Name}.");
    });

    // ---- entity tools ----

    private static void EnsureCatalog()
    {
        PluginCatalog.StartLoading();
    }

    private ToolResult ListPlacements(JsonObject a)
    {
        EnsureCatalog();
        string q = S(a, "query") ?? "";
        string kind = S(a, "kind") ?? "all";
        int limit = Math.Clamp(I(a, "limit") ?? 40, 1, 500);
        var all = PluginCatalog.AllPlacements()
            .Where(p => kind == "all" || (kind == "trigger") == p.Plugin.Trigger)
            .Where(p => q.Length == 0 || p.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Plugin.Source.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var arr = new JsonArray();
        foreach (var p in all.Take(limit))
        {
            var data = new JsonObject();
            foreach (var (k, v) in p.Data) data[k] = FromValue(v);
            arr.Add(new JsonObject
            {
                ["type"] = p.Plugin.Name, ["placement"] = p.Name, ["trigger"] = p.Plugin.Trigger,
                ["source"] = p.Plugin.Source, ["minNodes"] = p.Plugin.MinNodes, ["defaults"] = data
            });
        }
        var res = ToolResult.Json(new JsonObject { ["total"] = all.Count, ["loading"] = PluginCatalog.Loading, ["results"] = arr });
        return res;
    }

    private ToolResult AddEntity(JsonObject a)
    {
        EnsureCatalog();
        return Edit(() =>
        {
            var map = MapOrThrow();
            var r = FindRoom(Req(a, "room"));
            var el = CreateEntity(map, a, MapModel.NextId(map), out bool trigger, out bool known);
            (trigger ? r.TriggersEl : r.EntitiesEl).Children.Add(el);
            string note = known ? "" : " (type not found in installed Lönn plugins; placed without defaults)";
            return (r.El, $"Added {(trigger ? "trigger" : "entity")} {el.Name} #{el.Int("id")} at ({el.Num("x")},{el.Num("y")}) in {r.Name}{note}.");
        });
    }

    private ToolResult UpdateEntity(JsonObject a) => Edit(() =>
    {
        var r = FindRoom(Req(a, "room"));
        int id = I(a, "id") ?? throw new ToolException("Missing 'id'.");
        var (el, _) = FindEntity(r, id);
        if (D(a, "x") is double x) MapCanvas.SetNum(el, "x", (float)x);
        if (D(a, "y") is double y) MapCanvas.SetNum(el, "y", (float)y);
        ApplyAttrs(el, a, "id", "x", "y");
        if (a["remove_attrs"] is JsonArray rm)
            foreach (var k in rm) if (k?.ToString() is { } key && key is not ("id" or "x" or "y")) el.Remove(key);
        if (a.ContainsKey("nodes"))
        {
            el.Children.RemoveAll(c => c.Name == "node");
            foreach (var (nx, ny) in Nodes(a))
            {
                var n = new BinElement("node");
                MapCanvas.SetNum(n, "x", nx);
                MapCanvas.SetNum(n, "y", ny);
                el.Children.Add(n);
            }
        }
        return (r.El, $"Updated {el.Name} #{id} in {r.Name}.");
    });

    private ToolResult RemoveEntity(JsonObject a) => Edit(() =>
    {
        var r = FindRoom(Req(a, "room"));
        int id = I(a, "id") ?? throw new ToolException("Missing 'id'.");
        var (el, trig) = FindEntity(r, id);
        (trig ? r.TriggersEl : r.EntitiesEl).Children.Remove(el);
        return (r.El, $"Removed {el.Name} #{id} from {r.Name}.");
    });

    private ToolResult SearchEntities(JsonObject a) => _host.Invoke(() =>
    {
        string name = S(a, "name") ?? "", attr = S(a, "attr") ?? "", value = S(a, "value") ?? "", room = S(a, "room") ?? "";
        if (room.StartsWith("lvl_")) room = room[4..];
        int limit = Math.Clamp(I(a, "limit") ?? 100, 1, 2000), total = 0;
        var arr = new JsonArray();
        foreach (var r in MapModel.Levels(MapOrThrow()).Children.Select(l => new Room(l)))
        {
            if (room.Length > 0 && !string.Equals(r.Name, room, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (list, trig) in new[] { (r.EntitiesEl, false), (r.TriggersEl, true) })
                foreach (var e in list.Children)
                {
                    if (name.Length > 0 && !e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (attr.Length > 0 && e.Find(attr) == null) continue;
                    if (value.Length > 0 && !(attr.Length > 0 ? e.Str(attr) : string.Join("\n", e.Attrs.Select(x => x.Value))).Contains(value, StringComparison.OrdinalIgnoreCase)) continue;
                    if (total++ >= limit) continue;
                    var o = EntityJson(e, trig);
                    o["room"] = r.Name;
                    arr.Add(o);
                }
        }
        return ToolResult.Json(new JsonObject { ["total"] = total, ["results"] = arr });
    });

    // ---- game tools ----

    private ToolResult GameState()
    {
        var st = _host.Game.GetStateAsync().GetAwaiter().GetResult();
        if (st == null) return ToolResult.Text("Game not reachable. Start Celeste with Everest debug mode / remote control enabled (port 32270).", true);
        return ToolResult.Json(new JsonObject
        {
            ["inLevel"] = st.InLevel, ["sid"] = st.Sid, ["side"] = st.Side, ["room"] = st.Room, ["map"] = st.Map, ["dead"] = st.Dead,
            ["player"] = st.Player is PointF p ? new JsonArray(p.X, p.Y) : null,
            ["roomBounds"] = st.InLevel ? new JsonArray(st.Bounds.X, st.Bounds.Y, st.Bounds.Width, st.Bounds.Height) : null
        });
    }

    private ToolResult Teleport(JsonObject a)
    {
        var (path, room, dirty) = _host.Invoke(() => (_host.Path ?? throw new ToolException("No map is open."), FindRoom(Req(a, "room")).Name, _host.Dirty));
        PointF? local = D(a, "x") is double x && D(a, "y") is double y ? new PointF((float)x, (float)y) : null;
        string res = _host.Game.TeleportAsync(path, room, local, B(a, "exact")).GetAwaiter().GetResult();
        if (dirty) res += "\nNote: the map has unsaved changes; the game plays the saved file (save_map, then game_reload).";
        return ToolResult.Text(res, res.StartsWith("ERROR"));
    }

    private ToolResult GameReload()
    {
        string path = _host.Invoke(() => _host.Path) ?? throw new ToolException("No map is open.");
        string res = _host.Game.ReloadMapAsync(path).GetAwaiter().GetResult();
        return ToolResult.Text(res, res.StartsWith("ERROR"));
    }

    private static ToolResult ReadLog(JsonObject a)
    {
        string? dir = GameAssets.CelesteDir;
        string file = dir == null ? "" : System.IO.Path.Combine(dir, "log.txt");
        if (!File.Exists(file)) throw new ToolException("Celeste log.txt not found.");
        int n = Math.Clamp(I(a, "lines") ?? 80, 1, 2000);
        string f = S(a, "filter") ?? "";
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var q = new Queue<string>();
        while (sr.ReadLine() is { } line)
        {
            if (f.Length > 0 && !line.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            q.Enqueue(line);
            if (q.Count > n) q.Dequeue();
        }
        return ToolResult.Text(q.Count == 0 ? "(no matching lines)" : string.Join("\n", q));
    }
}
