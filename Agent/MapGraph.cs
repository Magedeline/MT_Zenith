using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

/// <summary>
/// Map-level connectivity: which rooms touch, where their border openings line up, which openings lead into a wall or
/// into nothing, which rooms the player can reach from the start room, and (detailed mode) whether each room can
/// actually be crossed from each entry with <see cref="RoomAnalysis"/>. The map-wide verifier for an agent building a level.
/// </summary>
public static class MapGraph
{
    public sealed class Opening
    {
        public required Room Room;
        public required RoomExit Exit;
        /// <summary>World-pixel span along the shared edge.</summary>
        public int From, To;
        public Room? Neighbor;
        public Opening? Partner;
        /// <summary>connected | blocked (a touching room has solid tiles there) | void (no room beyond)</summary>
        public string Status = "void";
        public string Key => $"{Room.Name}:{Exit.Side}:{Exit.From}-{Exit.To}";
        public IEnumerable<(int X, int Y)> Cells => Exit.Cells(Room.Cols, Room.Rows);
    }

    public sealed class Result
    {
        public readonly List<Room> Rooms = new();
        public readonly Dictionary<Room, RoomAnalysis> Built = new();
        public readonly List<Opening> Openings = new();
        public Room? Start;
        public readonly HashSet<Room> ReachableRooms = new();
        public readonly HashSet<Room> TraversableRooms = new();
        /// <summary>Rooms whose exits were only reachable with the advanced movement budget during the traversal.</summary>
        public readonly HashSet<Room> AdvancedRooms = new();
        public readonly List<string> Issues = new();
        public readonly Dictionary<string, List<string>> Internal = new(); // "room|entry" -> exits reached (basic tech)
        public readonly List<string> EndMarkers = new();
    }

    private static bool IsEndMarker(BinElement e)
    {
        string n = e.Name.ToLowerInvariant();
        return n.Contains("blackgem") || n.Contains("heartgem") || n.EndsWith("heart") || n.Contains("finalboss")
               || n.Contains("goldenberry") || (n.Contains("cassette") && !n.Contains("block"));
    }

    /// <summary>Connectivity from borders and positions; with <paramref name="detailed"/> each room is also crossed with the movement model.</summary>
    public static Result Analyze(MapBin map, bool detailed, int detailLimit = 60, Func<Room, bool>? filter = null)
    {
        var res = new Result();
        foreach (var l in MapModel.Levels(map).Children)
        {
            var r = new Room(l);
            if (filter != null && !filter(r)) continue;
            res.Rooms.Add(r);
            res.Built[r] = RoomAnalysis.Build(r);
            foreach (var ex in res.Built[r].Exits)
            {
                var op = new Opening { Room = r, Exit = ex };
                if (ex.Side is "left" or "right") { op.From = r.Y + ex.From * 8; op.To = r.Y + (ex.To + 1) * 8; }
                else { op.From = r.X + ex.From * 8; op.To = r.X + (ex.To + 1) * 8; }
                res.Openings.Add(op);
            }
            if (r.EntitiesEl.Children.Any(IsEndMarker)) res.EndMarkers.Add(r.Name);
        }
        res.Start = res.Rooms.FirstOrDefault(r => r.EntitiesEl.Children.Any(MapModel.IsSpawn));

        // pair openings across shared edges
        foreach (var a in res.Openings)
        {
            foreach (var b in res.Rooms)
            {
                if (b == a.Room || !Touches(a, b)) continue;
                a.Neighbor = b;
                string want = Opposite(a.Exit.Side);
                var partner = res.Openings.FirstOrDefault(o => o.Room == b && o.Exit.Side == want && Math.Min(a.To, o.To) - Math.Max(a.From, o.From) >= 8);
                if (partner != null) { a.Partner = partner; a.Status = "connected"; }
                else a.Status = "blocked";
                break;
            }
        }

        // room graph (undirected on connections)
        var adj = res.Rooms.ToDictionary(r => r, _ => new HashSet<Room>());
        foreach (var o in res.Openings)
            if (o.Status == "connected" && o.Neighbor != null) { adj[o.Room].Add(o.Neighbor); adj[o.Neighbor].Add(o.Room); }
        if (res.Start != null)
        {
            var q = new Queue<Room>(); q.Enqueue(res.Start); res.ReachableRooms.Add(res.Start);
            while (q.Count > 0)
                foreach (var n in adj[q.Dequeue()]) if (res.ReachableRooms.Add(n)) q.Enqueue(n);
        }

        foreach (var o in res.Openings)
        {
            if (o.Status == "blocked") res.Issues.Add($"{o.Room.Name}: opening {o.Exit.Label} runs into solid tiles of {o.Neighbor!.Name}; carve a matching opening there (connect_rooms).");
            else if (o.Status == "void" && o.Exit.Side != "bottom") res.Issues.Add($"{o.Room.Name}: opening {o.Exit.Label} leads to no room (the player walks/jumps out into nothing).");
        }
        foreach (var r in res.Rooms)
        {
            if (!r.EntitiesEl.Children.Any(MapModel.IsSpawn)) res.Issues.Add($"{r.Name}: no player spawn (dying here would crash the level).");
            if (res.Start != null && !res.ReachableRooms.Contains(r)) res.Issues.Add($"{r.Name}: not connected to the start room {res.Start.Name}.");
            if (adj[r].Count == 0 && res.Rooms.Count > 1) res.Issues.Add($"{r.Name}: isolated (no opening lines up with a neighbour).");
        }
        if (res.Start == null) res.Issues.Add("No room has a player spawn, so the level has no start.");
        if (res.EndMarkers.Count == 0 && res.Rooms.Count > 1) res.Issues.Add("No end marker found (crystal heart, golden berry, cassette or boss); the level has no goal yet.");

        if (detailed && res.Start != null) Traverse(res, detailLimit);
        return res;
    }

    private static bool Touches(Opening a, Room b)
    {
        var r = a.Room.Bounds;
        var o = b.Bounds;
        return a.Exit.Side switch
        {
            "right" => o.Left == r.Right && Overlap(a.From, a.To, o.Top, o.Bottom),
            "left" => o.Right == r.Left && Overlap(a.From, a.To, o.Top, o.Bottom),
            "bottom" => o.Top == r.Bottom && Overlap(a.From, a.To, o.Left, o.Right),
            _ => o.Bottom == r.Top && Overlap(a.From, a.To, o.Left, o.Right),
        };
    }

    private static bool Overlap(int a0, int a1, int b0, int b1) => Math.Min(a1, b1) - Math.Max(a0, b0) >= 8;

    private static string Opposite(string side) => side switch { "left" => "right", "right" => "left", "top" => "bottom", _ => "top" };

    /// <summary>Directed traversal from the start spawn through (room, entry) states with the Basic movement model.</summary>
    private static void Traverse(Result res, int limit)
    {
        var start = res.Start!;
        var seen = new HashSet<string>();
        var q = new Queue<(Room Room, Opening? Entry)>();
        q.Enqueue((start, null));
        seen.Add(start.Name + "|spawn");
        int analyzed = 0;
        var cache = new Dictionary<Room, RoomAnalysis>();
        while (q.Count > 0)
        {
            var (room, entry) = q.Dequeue();
            if (analyzed++ > limit) { res.Issues.Add($"Detailed traversal stopped after {limit} room entries (raise detail_limit to continue)."); break; }
            if (!cache.TryGetValue(room, out var an)) cache[room] = an = RoomAnalysis.Build(room);
            IEnumerable<(int, int)>? starts = entry == null ? null : entry.Partner!.Cells.ToList();
            if (entry == null && an.Spawn == null) continue;
            var ownOpenings = res.Openings.Where(o => o.Room == room).ToList();
            bool connectedExit(Opening o) => an.Exits.FirstOrDefault(e => e.Side == o.Exit.Side && e.From == o.Exit.From) is { Reached: true } && o.Status == "connected";
            // basic movement first; when no connected exit is reachable, see whether advanced tech would do it
            an.Run(Tech.Basic, starts);
            string tech = "basic";
            if (!ownOpenings.Any(connectedExit) && ownOpenings.Any(o => o.Status == "connected"))
            {
                an.Run(Tech.Advanced, starts);
                if (ownOpenings.Any(connectedExit)) { tech = "advanced"; res.AdvancedRooms.Add(room); }
            }
            res.TraversableRooms.Add(room);
            var reached = new List<string>();
            foreach (var o in ownOpenings)
            {
                var ex = an.Exits.FirstOrDefault(e => e.Side == o.Exit.Side && e.From == o.Exit.From);
                if (ex is not { Reached: true }) continue;
                reached.Add(o.Exit.Label + (o.Status == "connected" ? $" -> {o.Neighbor!.Name}" : $" ({o.Status})") + (tech == "advanced" ? " [advanced]" : ""));
                if (o.Status == "connected" && seen.Add(o.Neighbor!.Name + "|" + o.Partner!.Key))
                    q.Enqueue((o.Neighbor!, o));
            }
            string key = room.Name + "|" + (entry == null ? "spawn" : $"from {entry.Partner!.Exit.Label}");
            res.Internal[key] = reached;
            string where = entry == null ? "at the spawn" : "from " + entry.Partner!.Exit.Label;
            if (!ownOpenings.Any(connectedExit) && ownOpenings.Any(o => o.Status == "connected"))
                res.Issues.Add($"{room.Name} entered {where}: no connected exit is reachable even with advanced tech (analyze_room for details).");
            else if (tech == "advanced")
                res.Issues.Add($"{room.Name} entered {where}: exits need advanced tech (long jump/climb or platform momentum); fine for expert+, check the intended tier.");
        }
        foreach (var r in res.ReachableRooms)
            if (!res.TraversableRooms.Contains(r)) res.Issues.Add($"{r.Name}: connected on paper, but no traversal from the start reaches it.");
    }

    public static JsonObject ToJson(Result res, bool detailed)
    {
        var rooms = new JsonArray();
        foreach (var r in res.Rooms)
        {
            var ops = new JsonArray();
            foreach (var o in res.Openings.Where(o => o.Room == r))
            {
                var oj = new JsonObject { ["side"] = o.Exit.Side, ["cells"] = $"{o.Exit.From}-{o.Exit.To}", ["status"] = o.Status };
                if (o.Neighbor != null) oj["neighbor"] = o.Neighbor.Name;
                ops.Add(oj);
            }
            var rj = new JsonObject
            {
                ["name"] = r.Name, ["x"] = r.X, ["y"] = r.Y, ["width"] = r.W, ["height"] = r.H,
                ["spawn"] = r.EntitiesEl.Children.Any(MapModel.IsSpawn),
                ["openings"] = ops,
                ["connected_to_start"] = res.ReachableRooms.Contains(r),
            };
            if (detailed)
            {
                rj["traversable_from_start"] = res.TraversableRooms.Contains(r);
                var inner = new JsonObject();
                foreach (var (k, v) in res.Internal.Where(kv => kv.Key.StartsWith(r.Name + "|")))
                    inner[k[(r.Name.Length + 1)..]] = v.Count == 0 ? "no exit reachable" : string.Join(", ", v);
                if (inner.Count > 0) rj["entries"] = inner;
            }
            rooms.Add(rj);
        }
        return new JsonObject
        {
            ["start_room"] = res.Start?.Name,
            ["rooms"] = res.Rooms.Count,
            ["connections"] = res.Openings.Count(o => o.Status == "connected") / 2,
            ["connected_to_start"] = res.ReachableRooms.Count,
            ["traversable_from_start"] = detailed ? res.TraversableRooms.Count : null,
            ["rooms_needing_advanced_tech"] = detailed ? new JsonArray(res.AdvancedRooms.Select(r => (JsonNode)r.Name).ToArray()) : null,
            ["end_markers_in"] = new JsonArray(res.EndMarkers.Select(x => (JsonNode)x).ToArray()),
            ["issues"] = new JsonArray(res.Issues.Take(120).Select(x => (JsonNode)x).ToArray()),
            ["room_list"] = rooms,
            ["about"] = "Openings are runs of empty border cells. connected = a touching room has a matching opening; blocked = it has solid tiles there; " +
                        "void = nothing beyond (a bottom void is a death pit). Detailed mode walks the map from the start spawn with the basic movement model " +
                        "(jump, one dash, climb, rides on moving blocks), falling back to the advanced budget where basic gets stuck, and lists, per room and entry, which exits can be reached."
        };
    }
}
