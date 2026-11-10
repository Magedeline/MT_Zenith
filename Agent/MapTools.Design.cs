using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

// Design tools: whole rooms in one call, shape drawing, atomic batches, room connection, reachability analysis and
// persistent design notes. These are what let an agent build and verify a complex map instead of poking single tiles.
public sealed partial class MapTools
{
    private int _batchDepth;
    private readonly object _notesGate = new();

    private static JsonObject ArrayP(string description, JsonObject items) =>
        new() { ["type"] = "array", ["description"] = description, ["items"] = items };

    private static JsonObject RowsP(string description) => ArrayP(description, new JsonObject { ["type"] = "string" });

    private static JsonObject SideP => Enum("Side of the room.", "left", "right", "top", "bottom");

    private const string OpsHelp =
        "Each op: {op, layer?('fg'|'bg', default fg), tile?(tileset char, default '1'; '0' clears)} plus: " +
        "rect {x,y,w,h} fills; outline {x,y,w,h,thickness?} draws a hollow frame; hline {x,y,len}; vline {x,y,len}; line {x1,y1,x2,y2}; " +
        "fill {x,y} flood-fills the region of the cell's char; replace {from,to,x?,y?,w?,h?} swaps one char for another; " +
        "stairs {x,y,steps,dir:'up-right'|'up-left'|'down-right'|'down-left',step_w?,step_h?} builds a solid staircase whose base is row y; " +
        "pillar {x,y,h} = vline; platform {x,y,len} = hline; " +
        "exit {side,from,to,depth?} clears an opening in the border (from/to are cell indices along that side, depth default 2); " +
        "border {thickness?} draws the room outline. Coordinates are cells (8px). Clipped to the room.";

    private void RegisterDesign()
    {
        Add("analyze_room", "Reachability check of a room with an approximate Celeste movement model (jump, fall, one dash, climb with stamina, " +
            "wall jumps, swimming, springs/boosters/bumpers/feathers; spikes/spinners/lightning kill; jump-throughs are one-way). Reports which border " +
            "exits and items the player can reach from the spawn (or from an entry opening), the minimum tech per exit (none / dash / advanced / unreachable), " +
            "hazard pressure along the reachable space, a route per exit and hints. Optionally renders an overlay (green = standable, blue = airborne, red = lethal). " +
            "Run it after building or editing a room; fix what it flags before moving on.", true,
            Props(("room", RoomP),
                  ("entry", P("string", "Analyze from an entry instead of the spawn: 'left', 'right', 'top', 'bottom' (that side's openings) or 'x,y' (a cell).")),
                  ("render", P("boolean", "Also return the overlay image (default false).")),
                  ("scale", P("number", "Render scale (default fit to 1600px)."))), new[] { "room" }, AnalyzeRoom);
        Add("analyze_map", "Map connectivity: which rooms touch and where their border openings line up (connected / blocked by solids / leading nowhere), " +
            "the start room (first room with a spawn), rooms not connected to it, isolated rooms, end markers, and with detailed=true a walk through the whole " +
            "map from the start spawn with the basic movement model that tells, per room and entry, which exits are reachable. Run before saving a multi-room map.", true,
            Props(("detailed", P("boolean", "Also traverse every room with the movement model (default true; slower on huge maps).")),
                  ("detail_limit", P("integer", "Max room entries to traverse in detail (default 80).")),
                  ("filter", P("string", "Only rooms whose name contains this."))), None, AnalyzeMap);
        Add("build_room", "Create a complete room in ONE call (one undo step): placement (x/y, or attach to an existing room's side so the edges touch), " +
            "size (width/height in px or a size tier), fill ('border' = solid outline, 'solid', 'empty'), optional full fg/bg tile rows, draw ops (see draw_tiles), " +
            "exits carved in the border, a player spawn (auto-placed on the first standable floor unless given or spawn=false), entities and triggers " +
            "(same fields as add_entity: name, x, y, attrs, nodes, placement), room attrs, and finally an analyze_room report of the result. " +
            "Prefer this over add_room + many set_tiles calls.", false,
            Props(("name", P("string", "Room name.")),
                  ("x", P("integer", "World x (multiple of 8).")), ("y", P("integer", "World y.")),
                  ("attach", P("object", "{room, side:'left'|'right'|'top'|'bottom', align?:'start'|'center'|'end', offset?:px along the shared edge, gap?:px} places the new room against that room's side.")),
                  ("width", P("integer", "Width in px (default 320 or from size).")), ("height", P("integer", "Height in px (default 184 or from size).")),
                  ("size", SizeP), ("shape", ShapeP),
                  ("fill", Enum("Initial tiles when no fg rows are given (default border).", "border", "solid", "empty")),
                  ("tile", P("string", "Tileset char for fill/border/ops (default '1').")),
                  ("fg", RowsP("Full fg tile rows (shorter rows are padded with '0').")), ("bg", RowsP("Full bg tile rows.")),
                  ("ops", ArrayP("Draw ops applied after fill/fg. " + OpsHelp, new JsonObject { ["type"] = "object" })),
                  ("exits", ArrayP("Openings to carve: {side, from, to, depth?} in cells.", new JsonObject { ["type"] = "object" })),
                  ("spawn", P("object", "{x,y} room-local px of the player spawn; omit for automatic placement; false for none.")),
                  ("entities", ArrayP("Entities: {name, x, y, attrs?, nodes?, placement?}.", new JsonObject { ["type"] = "object" })),
                  ("triggers", ArrayP("Triggers: {name, x, y, width?, height?, attrs?, nodes?}.", new JsonObject { ["type"] = "object" })),
                  ("attrs", AttrsP),
                  ("analyze", P("boolean", "Append an analyze_room report (default true)."))), new[] { "name" }, BuildRoom);
        Add("draw_tiles", "Draw shapes into a room's tile layers in one undo step. " + OpsHelp, false,
            Props(("room", RoomP), ("ops", ArrayP("Ops, applied in order.", new JsonObject { ["type"] = "object" }))), new[] { "room", "ops" }, DrawTiles);
        Add("connect_rooms", "Carve a matching opening between two rooms that share an edge (both fg layers), optionally add a floor under a side opening " +
            "and a player spawn in the second room beside it. Reports where the opening is in each room's cells.", false,
            Props(("room_a", P("string", "First room.")), ("room_b", P("string", "Second room (must touch room_a).")),
                  ("width", P("integer", "Opening size in cells (default 4; for top/bottom openings this is the width, for side openings the height).")),
                  ("at", P("integer", "World px along the shared edge where the opening starts (default: centred on the overlap).")),
                  ("depth", P("integer", "Cells cleared inward on each side (default 2).")),
                  ("floor", P("boolean", "Side openings: ensure solid floor cells under the opening in both rooms (default true).")),
                  ("spawn", P("boolean", "Add a player spawn in room_b next to the opening when it has none (default true).")),
                  ("tile", P("string", "Tileset char for the floor (default '1')."))), new[] { "room_a", "room_b" }, ConnectRooms);
        Add("duplicate_room", "Copy a room (tiles, entities, triggers, decals; fresh ids) to a new position, optionally mirrored horizontally.", false,
            Props(("room", RoomP), ("name", P("string", "Name for the copy (default: unique variant of the original).")),
                  ("x", P("integer", "World x (default: right of the map).")), ("y", P("integer", "World y (default: same row).")),
                  ("mirror", P("boolean", "Mirror the copy left-right, including spike directions and nodes (default false)."))), new[] { "room" }, DuplicateRoom);
        Add("batch", "Run several tools as ONE undo step (and one round trip). steps: [{tool, args}]. Edit tools and read tools may be mixed " +
            "(e.g. build_room, draw_tiles, add_entity, then analyze_room). With atomic=true (default) any failing step rolls back every edit of the batch.", true,
            Props(("steps", ArrayP("Tool calls in order.", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["tool"] = P("string", "Tool name."), ["args"] = P("object", "Arguments.") } })),
                  ("atomic", P("boolean", "Roll everything back when a step fails (default true).")),
                  ("stop_on_error", P("boolean", "Stop at the first failing step (default true).")),
                  ("max_text", P("integer", "Characters of each step's text result to keep (default 3000)."))), new[] { "steps" }, Batch);
        Add("design_notes", "Persistent design notes for the open map, kept beside it as <map>.design.json and shown to the agent at the start of each turn: " +
            "the concept, difficulty tier, room-by-room plan, open todos and a log of decisions. Use it to plan before building and to keep state across long sessions. " +
            "action read returns everything (or one section); write replaces a section; append adds a line; delete removes a section.", true,
            Props(("action", Enum("What to do.", "read", "write", "append", "delete")),
                  ("section", P("string", "Section name, e.g. concept, tier, plan, rooms, todo, log, mechanics.")),
                  ("text", P("string", "Text for write/append."))), new[] { "action" }, DesignNotes);
    }

    // ---- analysis ----

    private static IEnumerable<(int X, int Y)>? ParseEntry(string? entry, RoomAnalysis built)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        entry = entry.Trim().ToLowerInvariant();
        if (entry is "left" or "right" or "top" or "bottom")
        {
            var cells = built.Exits.Where(e => e.Side == entry).SelectMany(e => e.Cells(built.Cols, built.Rows)).ToList();
            if (cells.Count == 0) throw new ToolException($"The room has no opening on its {entry} side.");
            return cells;
        }
        var parts = entry.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], out int cx) && int.TryParse(parts[1], out int cy)) return new[] { (cx, cy) };
        throw new ToolException("entry must be left, right, top, bottom or 'x,y' in cells.");
    }

    private ToolResult AnalyzeRoom(JsonObject a)
    {
        bool render = B(a, "render");
        var (job, boxes, name, report, analysis) = _host.Invoke(() =>
        {
            var r = FindRoom(Req(a, "room"));
            var built = RoomAnalysis.Build(r);
            var starts = ParseEntry(S(a, "entry"), built);
            var (basic, rep) = RoomAnalysis.Report(r, starts);
            rep["room"] = r.Name;
            return (render ? RoomRenderJob.From(r, _host.FgTiler, _host.BgTiler) : null, render ? EntityBoxes(r) : null, r.Name, rep, basic);
        });
        var res = ToolResult.Json(report);
        if (!render || job == null) return res;
        var png = RenderPng(job, boxes!, D(a, "scale"), $"Room {name} reachability", (g, scale) => DrawOverlay(g, scale, analysis));
        foreach (var c in png.Content) res.Content.Add(c?.DeepClone());
        return res;
    }

    internal static void DrawOverlay(Graphics g, float scale, RoomAnalysis an)
    {
        float cs = 8 * scale;
        for (int y = 0; y < an.Rows; y++)
            for (int x = 0; x < an.Cols; x++)
            {
                int c = an.OverlayColor(x, y);
                if (c == 0) continue;
                using var b = new SolidBrush(Color.FromArgb(c));
                g.FillRectangle(b, x * cs, y * cs, cs, cs);
            }
        foreach (var e in an.Exits)
        {
            using var pen = new Pen(e.Reached ? Color.DeepSkyBlue : Color.Orange, Math.Max(1f, scale));
            foreach (var (x, y) in e.Cells(an.Cols, an.Rows)) g.DrawRectangle(pen, x * cs, y * cs, cs, cs);
        }
        if (an.Spawn is { } s)
        {
            using var pen = new Pen(Color.Yellow, Math.Max(1f, scale * 1.5f));
            g.DrawEllipse(pen, s.X * cs - cs / 2, s.Y * cs - cs, cs * 2, cs * 2);
        }
    }

    private ToolResult AnalyzeMap(JsonObject a)
    {
        bool detailed = B(a, "detailed", true);
        int limit = Math.Clamp(I(a, "detail_limit") ?? 80, 1, 2000);
        string f = S(a, "filter") ?? "";
        var json = _host.Invoke(() =>
        {
            var res = MapGraph.Analyze(MapOrThrow(), detailed, limit, f.Length == 0 ? null : r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
            return MapGraph.ToJson(res, detailed);
        });
        return ToolResult.Json(json);
    }

    // ---- drawing ----

    private sealed record OpResult(int Cells, List<string> Log, HashSet<char> Unknown);

    /// <summary>Applies draw ops to a room's grids and commits them. Runs on the map thread.</summary>
    private OpResult ApplyOps(Room r, JsonArray ops, char defaultTile)
    {
        var log = new List<string>();
        var unknown = new HashSet<char>();
        int total = 0;
        bool fgTouched = false, bgTouched = false;
        foreach (var node in ops)
        {
            if (node is not JsonObject op) throw new ToolException("Each op must be an object.");
            string kind = (S(op, "op") ?? "").ToLowerInvariant();
            bool fg = Fg(op);
            var grid = r.Grid(fg);
            var tiler = fg ? _host.FgTiler : _host.BgTiler;
            char tile = (S(op, "tile") ?? defaultTile.ToString()) is { Length: 1 } t ? t[0] : throw new ToolException($"op {kind}: 'tile' must be one char.");
            if (tile != '0' && tiler != null && !tiler.Has(tile)) unknown.Add(tile);
            int n = 0;
            void Put(int x, int y, char c) { if ((uint)x < (uint)grid.Cols && (uint)y < (uint)grid.Rows) { grid[x, y] = c; n++; } }
            void Rect(int x, int y, int w, int h, char c) { for (int yy = y; yy < y + h; yy++) for (int xx = x; xx < x + w; xx++) Put(xx, yy, c); }
            int X() => I(op, "x") ?? 0;
            int Y() => I(op, "y") ?? 0;
            switch (kind)
            {
                case "rect": Rect(X(), Y(), I(op, "w") ?? I(op, "width") ?? 1, I(op, "h") ?? I(op, "height") ?? 1, tile); break;
                case "clear": Rect(X(), Y(), I(op, "w") ?? I(op, "width") ?? 1, I(op, "h") ?? I(op, "height") ?? 1, '0'); break;
                case "outline":
                {
                    int x = X(), y = Y(), w = I(op, "w") ?? I(op, "width") ?? grid.Cols, h = I(op, "h") ?? I(op, "height") ?? grid.Rows, th = I(op, "thickness") ?? 1;
                    for (int yy = y; yy < y + h; yy++)
                        for (int xx = x; xx < x + w; xx++)
                            if (xx < x + th || xx >= x + w - th || yy < y + th || yy >= y + h - th) Put(xx, yy, tile);
                    break;
                }
                case "border":
                {
                    int th = I(op, "thickness") ?? 1;
                    for (int yy = 0; yy < grid.Rows; yy++)
                        for (int xx = 0; xx < grid.Cols; xx++)
                            if (xx < th || xx >= grid.Cols - th || yy < th || yy >= grid.Rows - th) Put(xx, yy, tile);
                    break;
                }
                case "hline" or "platform": Rect(X(), Y(), I(op, "len") ?? I(op, "w") ?? 1, 1, tile); break;
                case "vline" or "pillar": Rect(X(), Y(), 1, I(op, "len") ?? I(op, "h") ?? 1, tile); break;
                case "line":
                    foreach (var c in MapCanvas.LineCells(new Point(I(op, "x1") ?? 0, I(op, "y1") ?? 0), new Point(I(op, "x2") ?? 0, I(op, "y2") ?? 0))) Put(c.X, c.Y, tile);
                    break;
                case "fill":
                    if (grid.Fill(X(), Y(), tile)) n++;
                    break;
                case "replace":
                {
                    char from = (S(op, "from") ?? "1")[0], to = (S(op, "to") ?? tile.ToString())[0];
                    int x = I(op, "x") ?? 0, y = I(op, "y") ?? 0, w = I(op, "w") ?? grid.Cols, h = I(op, "h") ?? grid.Rows;
                    for (int yy = Math.Max(0, y); yy < Math.Min(grid.Rows, y + h); yy++)
                        for (int xx = Math.Max(0, x); xx < Math.Min(grid.Cols, x + w); xx++)
                            if (grid[xx, yy] == from) { grid[xx, yy] = to; n++; }
                    break;
                }
                case "stairs":
                {
                    int x = X(), y = Y(), steps = Math.Clamp(I(op, "steps") ?? 4, 1, 400), sw = Math.Max(1, I(op, "step_w") ?? 1), sh = Math.Max(1, I(op, "step_h") ?? 1);
                    string dir = (S(op, "dir") ?? "up-right").ToLowerInvariant();
                    bool up = !dir.StartsWith("down"), right = !dir.EndsWith("left");
                    for (int s = 0; s < steps; s++)
                    {
                        int sx = x + (right ? s * sw : -(s + 1) * sw + 1);
                        int top = up ? y - (s + 1) * sh + 1 : y - (steps - s) * sh + 1;
                        Rect(sx, top, sw, y - top + 1, tile); // solid down to the base row
                    }
                    break;
                }
                case "exit":
                {
                    string side = (S(op, "side") ?? "").ToLowerInvariant();
                    int from = I(op, "from") ?? 0, to = I(op, "to") ?? from, depth = Math.Max(1, I(op, "depth") ?? 2);
                    if (to < from) (from, to) = (to, from);
                    switch (side)
                    {
                        case "left": Rect(0, from, depth, to - from + 1, '0'); break;
                        case "right": Rect(grid.Cols - depth, from, depth, to - from + 1, '0'); break;
                        case "top": Rect(from, 0, to - from + 1, depth, '0'); break;
                        case "bottom": Rect(from, grid.Rows - depth, to - from + 1, depth, '0'); break;
                        default: throw new ToolException("exit op needs side left/right/top/bottom.");
                    }
                    break;
                }
                default: throw new ToolException($"Unknown op '{kind}'. " + OpsHelp);
            }
            if (fg) fgTouched = true; else bgTouched = true;
            total += n;
            log.Add($"{kind}: {n} cells");
        }
        if (fgTouched) r.CommitGrid(true);
        if (bgTouched) r.CommitGrid(false);
        return new OpResult(total, log, unknown);
    }

    private ToolResult DrawTiles(JsonObject a) => Edit(() =>
    {
        var r = FindRoom(Req(a, "room"));
        if (a["ops"] is not JsonArray ops || ops.Count == 0) throw new ToolException("'ops' must be a non-empty array.");
        var res = ApplyOps(r, ops, '1');
        string msg = $"Drew in {r.Name}: {string.Join(", ", res.Log)} ({res.Cells} cells).";
        if (res.Unknown.Count > 0) msg += $" Warning: unknown tileset chars {string.Join("", res.Unknown)} (see list_tilesets).";
        return (r.El, msg);
    });

    // ---- building a room ----

    /// <summary>Creates an entity/trigger element from a spec (shared by add_entity and build_room). Runs on the map thread.</summary>
    private static BinElement CreateEntity(MapBin map, JsonObject spec, int id, out bool trigger, out bool known)
    {
        string name = Req(spec, "name");
        trigger = B(spec, "trigger");
        var def = trigger ? PluginCatalog.Trigger(name) : PluginCatalog.Entity(name);
        if (def == null && !spec.ContainsKey("trigger") && PluginCatalog.Trigger(name) is { } td) { def = td; trigger = true; }
        known = def != null;
        string? pn = S(spec, "placement");
        var placement = def?.Placements.FirstOrDefault(p => pn != null && string.Equals(p.Name, pn, StringComparison.OrdinalIgnoreCase))
                        ?? def?.Placements.FirstOrDefault();
        var src = placement?.Create() ?? new BinElement(name);
        if (trigger && placement == null) { src.Set("width", 16); src.Set("height", 16); }
        var el = new BinElement(name);
        el.Set("id", id);
        MapCanvas.SetNum(el, "x", (float)(D(spec, "x") ?? 0));
        MapCanvas.SetNum(el, "y", (float)(D(spec, "y") ?? 0));
        if (I(spec, "width") is int w) el.Set("width", w);
        if (I(spec, "height") is int h) el.Set("height", h);
        foreach (var at in src.Attrs) if (el.Find(at.Key) == null) el.Attrs.Add(at.Clone());
        ApplyAttrs(el, spec, "id", "x", "y");
        var nodes = Nodes(spec);
        for (int i = nodes.Count; i < (def?.MinNodes ?? 0); i++) nodes.Add((el.Num("x") + 16 * (i + 1), el.Num("y")));
        foreach (var (nx, ny) in nodes)
        {
            var n = new BinElement("node");
            MapCanvas.SetNum(n, "x", nx);
            MapCanvas.SetNum(n, "y", ny);
            el.Children.Add(n);
        }
        return el;
    }

    private static void FillRows(TileGrid g, JsonArray rows)
    {
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                string row = y < rows.Count ? rows[y]?.ToString() ?? "" : "";
                char c = x < row.Length ? row[x] : '0';
                g[x, y] = c == ' ' ? '0' : c;
            }
    }

    /// <summary>First cell standing on solid ground with headroom, scanning left to right, bottom up; null when the room has none.</summary>
    private static (int X, int Y)? AutoSpawnCell(Room r)
    {
        var g = r.Grid(true);
        for (int x = 1; x < g.Cols - 1; x++)
            for (int y = g.Rows - 2; y >= 1; y--)
                if (g[x, y] == '0' && g[x, y - 1] == '0' && g[x, y + 1] != '0') return (x, y);
        return null;
    }

    private ToolResult BuildRoom(JsonObject a)
    {
        EnsureCatalog();
        return Edit(() =>
        {
            var map = MapOrThrow();
            var preset = S(a, "size") is { Length: > 0 } tier ? Arg(() => RoomSizing.TierSize(tier, S(a, "shape") ?? "auto")) : new Size(320, 184);
            int w = Math.Max(8, MapModel.Snap8(I(a, "width") ?? preset.Width)), h = Math.Max(8, MapModel.Snap8(I(a, "height") ?? preset.Height));
            if (a["fg"] is JsonArray fgRowsForSize && !a.ContainsKey("width") && !a.ContainsKey("size"))
            {
                int cols = fgRowsForSize.Max(r => r?.ToString().Length ?? 0), rows = fgRowsForSize.Count;
                if (cols > 0 && rows > 0) { w = cols * 8; h = rows * 8; }
            }

            int x, y;
            string placed;
            if (a["attach"] is JsonObject at)
            {
                var anchor = FindRoom(Req(at, "room"));
                string side = (S(at, "side") ?? "right").ToLowerInvariant();
                string align = (S(at, "align") ?? "start").ToLowerInvariant();
                int gap = MapModel.Snap8(I(at, "gap") ?? 0);
                int? off = I(at, "offset");
                int Along(int anchorLen, int myLen) => off is int o ? MapModel.Snap8(o) : align switch
                {
                    "center" or "centre" or "middle" => MapModel.Snap8((anchorLen - myLen) / 2),
                    "end" or "bottom" or "right" => anchorLen - myLen,
                    _ => 0
                };
                switch (side)
                {
                    case "right": x = anchor.X + anchor.W + gap; y = anchor.Y + Along(anchor.H, h); break;
                    case "left": x = anchor.X - w - gap; y = anchor.Y + Along(anchor.H, h); break;
                    case "top" or "up": x = anchor.X + Along(anchor.W, w); y = anchor.Y - h - gap; break;
                    case "bottom" or "down": x = anchor.X + Along(anchor.W, w); y = anchor.Y + anchor.H + gap; break;
                    default: throw new ToolException("attach.side must be left, right, top or bottom.");
                }
                placed = $"attached to the {side} of {anchor.Name}";
            }
            else if (I(a, "x") is int ax && I(a, "y") is int ay) { x = MapModel.Snap8(ax); y = MapModel.Snap8(ay); placed = "at the given position"; }
            else
            {
                var b = MapModel.MapBounds(map);
                x = MapModel.Snap8(b.Right + 16); y = MapModel.Snap8(b.Top);
                placed = "right of the map";
            }

            string name = MapModel.UniqueName(map, Req(a, "name"));
            var level = MapModel.NewLevel(name, x, y, w, h);
            ApplyAttrs(level, a, "name", "x", "y", "width", "height");
            MapModel.Levels(map).Children.Add(level);
            var room = new Room(level);
            char tile = (S(a, "tile") ?? "1") is { Length: 1 } ts ? ts[0] : throw new ToolException("'tile' must be one char.");
            var notes = new List<string>();

            if (a["fg"] is JsonArray fgRows) { FillRows(room.Grid(true), fgRows); room.CommitGrid(true); notes.Add("fg rows set"); }
            else
            {
                string fill = (S(a, "fill") ?? "border").ToLowerInvariant();
                if (fill is not ("solid" or "border" or "empty")) throw new ToolException("fill must be border, solid or empty.");
                var g = room.Grid(true);
                if (fill != "empty")
                    for (int yy = 0; yy < g.Rows; yy++)
                        for (int xx = 0; xx < g.Cols; xx++)
                            if (fill == "solid" || xx == 0 || yy == 0 || xx == g.Cols - 1 || yy == g.Rows - 1) g[xx, yy] = tile;
                room.CommitGrid(true);
                notes.Add($"fill {fill}");
            }
            if (a["bg"] is JsonArray bgRows) { FillRows(room.Grid(false), bgRows); room.CommitGrid(false); notes.Add("bg rows set"); }
            if (a["ops"] is JsonArray ops && ops.Count > 0)
            {
                var res = ApplyOps(room, ops, tile);
                notes.Add($"{ops.Count} ops ({res.Cells} cells)");
                if (res.Unknown.Count > 0) notes.Add($"WARNING unknown tileset chars {string.Join("", res.Unknown)}");
            }
            if (a["exits"] is JsonArray exits && exits.Count > 0)
            {
                var exitOps = new JsonArray();
                foreach (var e in exits)
                {
                    if (e is not JsonObject eo) continue;
                    var op = (JsonObject)eo.DeepClone();
                    op["op"] = "exit";
                    exitOps.Add(op);
                }
                ApplyOps(room, exitOps, tile);
                notes.Add($"{exits.Count} exit(s) carved");
            }

            int id = MapModel.NextId(map);
            int unknownCount = 0;
            foreach (var (key, trig) in new[] { ("entities", false), ("triggers", true) })
                if (a[key] is JsonArray list)
                    foreach (var spec in list.OfType<JsonObject>())
                    {
                        var s = (JsonObject)spec.DeepClone();
                        if (trig && !s.ContainsKey("trigger")) s["trigger"] = true;
                        var el = CreateEntity(map, s, id++, out bool isTrigger, out bool known);
                        if (!known) unknownCount++;
                        (isTrigger ? room.TriggersEl : room.EntitiesEl).Children.Add(el);
                    }
            if (unknownCount > 0) notes.Add($"{unknownCount} entity type(s) unknown to the installed Lönn plugins (placed without defaults)");

            bool wantSpawn = a["spawn"] is not JsonValue sv || !(sv.TryGetValue(out bool sb) && !sb);
            if (wantSpawn && !room.EntitiesEl.Children.Any(MapModel.IsSpawn))
            {
                float? sx = null, sy = null;
                if (a["spawn"] is JsonObject so && D(so, "x") is double px && D(so, "y") is double py) { sx = (float)px; sy = (float)py; }
                else if (AutoSpawnCell(room) is { } c) { sx = c.X * 8 + 4; sy = (c.Y + 1) * 8; }
                if (sx != null)
                {
                    var p = new BinElement("player");
                    p.Set("id", id++);
                    MapCanvas.SetNum(p, "x", sx.Value);
                    MapCanvas.SetNum(p, "y", sy!.Value);
                    room.EntitiesEl.Children.Add(p);
                    notes.Add($"spawn at ({sx},{sy})");
                }
                else notes.Add("WARNING no standable floor found for an automatic spawn; add one with add_entity name='player'");
            }

            var overlap = MapModel.Levels(map).Children.Where(l => l != level).Select(l => new Room(l)).FirstOrDefault(o => o.Bounds.IntersectsWith(room.Bounds));
            var sb2 = new StringBuilder($"Built room {room.Name} {placed} at ({x},{y}) {w}x{h} [{RoomSizing.TierOf(w, h)}]: {string.Join("; ", notes)}.");
            if (overlap != null) sb2.Append($" WARNING: overlaps room {overlap.Name}.");
            if (B(a, "analyze", true))
            {
                var (_, rep) = RoomAnalysis.Report(room);
                sb2.Append("\nAnalysis: ").Append(rep.ToJsonString(JsonOut));
            }
            return (null, sb2.ToString());
        });
    }

    // ---- connecting rooms ----

    private ToolResult ConnectRooms(JsonObject a) => Edit(() =>
    {
        var map = MapOrThrow();
        var ra = FindRoom(Req(a, "room_a"));
        var rb = FindRoom(Req(a, "room_b"));
        if (ra.El == rb.El) throw new ToolException("room_a and room_b must differ.");
        int size = Math.Max(2, I(a, "width") ?? 4), depth = Math.Max(1, I(a, "depth") ?? 2);
        char tile = (S(a, "tile") ?? "1")[0];
        var A = ra.Bounds; var Bb = rb.Bounds;
        string sideA;
        int lo, hi; // overlap along the shared edge in world px
        if (A.Right == Bb.Left && Overlaps(A.Top, A.Bottom, Bb.Top, Bb.Bottom)) { sideA = "right"; lo = Math.Max(A.Top, Bb.Top); hi = Math.Min(A.Bottom, Bb.Bottom); }
        else if (A.Left == Bb.Right && Overlaps(A.Top, A.Bottom, Bb.Top, Bb.Bottom)) { sideA = "left"; lo = Math.Max(A.Top, Bb.Top); hi = Math.Min(A.Bottom, Bb.Bottom); }
        else if (A.Bottom == Bb.Top && Overlaps(A.Left, A.Right, Bb.Left, Bb.Right)) { sideA = "bottom"; lo = Math.Max(A.Left, Bb.Left); hi = Math.Min(A.Right, Bb.Right); }
        else if (A.Top == Bb.Bottom && Overlaps(A.Left, A.Right, Bb.Left, Bb.Right)) { sideA = "top"; lo = Math.Max(A.Left, Bb.Left); hi = Math.Min(A.Right, Bb.Right); }
        else throw new ToolException($"{ra.Name} and {rb.Name} do not share an edge. Move one with update_room/resize_room (or build_room attach) so they touch.");
        bool horizontal = sideA is "left" or "right"; // opening in a vertical edge: spans rows
        int span = hi - lo;
        if (span < size * 8) size = Math.Max(1, span / 8);
        int at = I(a, "at") is int given ? Math.Clamp(MapModel.Snap8(given), lo, hi - size * 8) : MapModel.Snap8(lo + (span - size * 8) / 2);
        string sideB = sideA switch { "left" => "right", "right" => "left", "top" => "bottom", _ => "top" };

        void Carve(Room r, string side, out int from, out int to)
        {
            int local = horizontal ? (at - r.Y) / 8 : (at - r.X) / 8;
            from = local; to = local + size - 1;
            ApplyOps(r, new JsonArray(new JsonObject { ["op"] = "exit", ["side"] = side, ["from"] = from, ["to"] = to, ["depth"] = depth }), tile);
        }
        Carve(ra, sideA, out int fa, out int ta);
        Carve(rb, sideB, out int fb, out int tb);
        var notes = new List<string>();
        if (horizontal && B(a, "floor", true))
        {
            foreach (var (r, side, to) in new[] { (ra, sideA, ta), (rb, sideB, tb) })
            {
                var g = r.Grid(true);
                int floorRow = to + 1;
                if (floorRow >= g.Rows) continue;
                int n = 0;
                for (int d = 0; d < depth; d++)
                {
                    int cx = side == "left" ? d : g.Cols - 1 - d;
                    if (g[cx, floorRow] == '0') { g[cx, floorRow] = tile; n++; }
                }
                if (n > 0) { r.CommitGrid(true); notes.Add($"{n} floor cells added in {r.Name}"); }
            }
        }
        if (B(a, "spawn", true) && !rb.EntitiesEl.Children.Any(MapModel.IsSpawn))
        {
            var g = rb.Grid(true);
            int cx = sideB switch { "left" => depth, "right" => g.Cols - 1 - depth, _ => fb + size / 2 }, cy;
            if (horizontal) cy = tb;
            else { cy = sideB == "top" ? depth + 1 : g.Rows - 2; while (cy < g.Rows - 1 && g[cx, cy + 1] == '0') cy++; }
            var p = new BinElement("player");
            p.Set("id", MapModel.NextId(map));
            MapCanvas.SetNum(p, "x", cx * 8 + 4);
            MapCanvas.SetNum(p, "y", (cy + 1) * 8);
            rb.EntitiesEl.Children.Add(p);
            notes.Add($"spawn added in {rb.Name} at ({cx * 8 + 4},{(cy + 1) * 8})");
        }
        return (null, $"Connected {ra.Name} ({sideA} cells {fa}-{ta}) with {rb.Name} ({sideB} cells {fb}-{tb}), {size} cells wide, {depth} deep, at world {(horizontal ? "y" : "x")}={at}." +
                      (notes.Count > 0 ? " " + string.Join("; ", notes) + "." : ""));
    });

    private static bool Overlaps(int a0, int a1, int b0, int b1) => Math.Min(a1, b1) - Math.Max(a0, b0) >= 16;

    // ---- duplicating ----

    private ToolResult DuplicateRoom(JsonObject a) => Edit(() =>
    {
        var map = MapOrThrow();
        var src = FindRoom(Req(a, "room"));
        var b = MapModel.MapBounds(map);
        int x = MapModel.Snap8(I(a, "x") ?? b.Right + 16), y = MapModel.Snap8(I(a, "y") ?? src.Y);
        var copy = MapModel.DuplicateLevel(map, src.El, x, y);
        if (S(a, "name") is { Length: > 0 } nm) copy.Set("name", MapModel.UniqueName(map, nm));
        var room = new Room(copy);
        if (B(a, "mirror"))
        {
            foreach (bool fg in new[] { true, false })
            {
                var g = room.Grid(fg);
                var m = new TileGrid(g.Cols, g.Rows);
                for (int yy = 0; yy < g.Rows; yy++) for (int xx = 0; xx < g.Cols; xx++) m[g.Cols - 1 - xx, yy] = g[xx, yy];
                for (int yy = 0; yy < g.Rows; yy++) for (int xx = 0; xx < g.Cols; xx++) g[xx, yy] = m[xx, yy];
                room.CommitGrid(fg);
            }
            foreach (string list in new[] { "entities", "triggers", "fgdecals", "bgdecals" })
                foreach (var e in copy.Child(list)?.Children ?? new())
                {
                    float w = e.Num("width");
                    MapCanvas.SetNum(e, "x", room.W - e.Num("x") - w);
                    foreach (var n in e.Children.Where(c => c.Name == "node")) MapCanvas.SetNum(n, "x", room.W - n.Num("x") - w);
                    if (EntityVisuals.SpikeDir(e.Name) is "Left" or "Right")
                        e.Name = e.Name.EndsWith("Left") ? e.Name[..^4] + "Right" : e.Name[..^5] + "Left";
                    if (list.EndsWith("decals") && e.Find("scaleX") is { } sx) sx.Value = -e.Num("scaleX");
                }
        }
        return (null, $"Duplicated {src.Name} as {room.Name} at ({x},{y}){(B(a, "mirror") ? ", mirrored" : "")}.");
    });

    // ---- batch ----

    private ToolResult Batch(JsonObject a)
    {
        if (a["steps"] is not JsonArray steps || steps.Count == 0) throw new ToolException("'steps' must be a non-empty array of {tool, args}.");
        bool atomic = B(a, "atomic", true), stop = B(a, "stop_on_error", true);
        int maxText = Math.Clamp(I(a, "max_text") ?? 3000, 200, 100000);
        return _host.Invoke(() =>
        {
            MapOrThrow();
            if (_batchDepth > 0) throw new ToolException("batch can't be nested.");
            bool anyEdit = steps.OfType<JsonObject>().Any(s => _tools.Find(t => t.Name == S(s, "tool")) is { ReadOnly: false });
            if (anyEdit && _host.ReadOnly) throw new ToolException("The map is read-only for AI; the batch contains edit tools.");
            if (anyEdit) _host.BeginEdit();
            _batchDepth++;
            var results = new JsonArray();
            int ok = 0, failed = 0;
            try
            {
                foreach (var node in steps)
                {
                    if (node is not JsonObject step) { failed++; results.Add(new JsonObject { ["ok"] = false, ["error"] = "step is not an object" }); if (stop) break; continue; }
                    string tool = S(step, "tool") ?? "";
                    var def = _tools.Find(t => t.Name == tool);
                    var rj = new JsonObject { ["tool"] = tool };
                    if (def == null || tool == "batch") { rj["ok"] = false; rj["error"] = "unknown tool"; failed++; results.Add(rj); if (stop) break; continue; }
                    ToolResult r;
                    try { r = def.Run(step["args"] as JsonObject ?? new JsonObject()); }
                    catch (ToolException ex) { r = ToolResult.Text(ex.Message, true); }
                    catch (Exception ex) { r = ToolResult.Text($"{ex.GetType().Name}: {ex.Message}", true); }
                    rj["ok"] = !r.IsError;
                    var texts = r.Content.OfType<JsonObject>().Where(c => c["type"]?.ToString() == "text").Select(c => c["text"]?.ToString() ?? "");
                    string text = string.Join("\n", texts);
                    rj["result"] = text.Length > maxText ? text[..maxText] + $"… (+{text.Length - maxText} chars)" : text;
                    int images = r.Content.OfType<JsonObject>().Count(c => c["type"]?.ToString() == "image");
                    if (images > 0) rj["images_dropped"] = images;
                    results.Add(rj);
                    if (r.IsError) { failed++; if (stop) break; }
                    else ok++;
                }
            }
            finally { _batchDepth--; }
            bool rolledBack = false;
            if (failed > 0 && atomic && anyEdit) { rolledBack = _host.Undo(); }
            if (anyEdit && !rolledBack) _host.EndEdit(null, $"batch: {ok} step(s)");
            else if (rolledBack) _host.EndEdit(null, "batch rolled back");
            _host.Log($"[AI] batch: {ok} ok, {failed} failed{(rolledBack ? ", rolled back" : "")}");
            return ToolResult.Json(new JsonObject
            {
                ["ok"] = failed == 0, ["completed"] = ok, ["failed"] = failed, ["rolled_back"] = rolledBack,
                ["steps"] = results
            });
        });
    }

    // ---- design notes ----

    private string? NotesPath() => _host.Path is { } p ? p + ".design.json" : null;

    private JsonObject LoadNotes()
    {
        string? path = NotesPath();
        if (path == null || !File.Exists(path)) return _memoryNotes ??= new JsonObject();
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private JsonObject? _memoryNotes;

    private void SaveNotes(JsonObject notes)
    {
        string? path = NotesPath();
        if (path == null) { _memoryNotes = notes; return; }
        File.WriteAllText(path, notes.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        }));
    }

    /// <summary>A short digest of the notes for the agent's per-turn context (plan/todo first), or null when there are none.</summary>
    public string? NotesSummary(int maxChars = 900)
    {
        lock (_notesGate)
        {
            var notes = LoadNotes();
            if (notes.Count == 0) return null;
            var sb = new StringBuilder("Design notes (design_notes read for all): ");
            foreach (var key in notes.Select(kv => kv.Key).OrderBy(k => k is "todo" ? 0 : k is "plan" ? 1 : k is "concept" or "tier" ? 2 : 3))
            {
                string v = notes[key]?.ToString() ?? "";
                string line = v.Replace("\r", "").Replace("\n", " | ");
                if (line.Length > 300) line = line[..300] + "…";
                sb.Append($"[{key}] {line} ");
                if (sb.Length > maxChars) { sb.Append('…'); break; }
            }
            return sb.ToString();
        }
    }

    private ToolResult DesignNotes(JsonObject a)
    {
        string action = Req(a, "action").ToLowerInvariant();
        string? section = S(a, "section");
        string text = S(a, "text") ?? "";
        lock (_notesGate)
        {
            var notes = LoadNotes();
            switch (action)
            {
                case "read":
                    if (section is { Length: > 0 }) return ToolResult.Text(notes[section]?.ToString() ?? $"(no section '{section}')");
                    if (notes.Count == 0) return ToolResult.Text("(no design notes yet; write a 'concept', 'tier', 'plan' and 'todo' before building)");
                    return ToolResult.Text(string.Join("\n\n", notes.Select(kv => $"## {kv.Key}\n{kv.Value}")));
                case "write":
                    if (string.IsNullOrEmpty(section)) throw new ToolException("write needs a section.");
                    notes[section] = text;
                    break;
                case "append":
                    if (string.IsNullOrEmpty(section)) throw new ToolException("append needs a section.");
                    string old = notes[section]?.ToString() ?? "";
                    notes[section] = old.Length == 0 ? text : old.TrimEnd() + "\n" + text;
                    break;
                case "delete":
                    if (string.IsNullOrEmpty(section)) throw new ToolException("delete needs a section.");
                    notes.Remove(section);
                    break;
                default: throw new ToolException("action must be read, write, append or delete.");
            }
            SaveNotes(notes);
            return ToolResult.Text($"{action} {section}: ok ({(NotesPath() is { } p ? "saved to " + p : "kept in memory until a map is open")}).");
        }
    }
}
