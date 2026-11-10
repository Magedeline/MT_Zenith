using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

/// <summary>Movement budget the analyzer assumes of the player.</summary>
public enum Tech
{
    /// <summary>Jump, fall, climb and wall-jump only (a dashless room).</summary>
    NoDash = 0,
    /// <summary>One dash per airtime, refilled on landing: the baseline every Celeste room is built for.</summary>
    Basic = 1,
    /// <summary>Hypers, supers, wavedashes, demodashes, long climbs: what advanced+ collab rooms expect.</summary>
    Advanced = 2
}

/// <summary>What a tile cell is to the player.</summary>
public enum CellKind : byte { Air, Solid, Platform, Hazard, Water, Dream }

/// <summary>An opening in a room's border: a run of passable border cells on one side.</summary>
public sealed class RoomExit
{
    public string Side = "";
    /// <summary>Inclusive cell range along the edge (rows for left/right, columns for top/bottom).</summary>
    public int From, To;
    public bool Reached;
    public Tech? MinTech;

    public IEnumerable<(int X, int Y)> Cells(int cols, int rows)
    {
        for (int i = From; i <= To; i++)
            yield return Side switch { "left" => (0, i), "right" => (cols - 1, i), "top" => (i, 0), _ => (i, rows - 1) };
    }

    public string Label => $"{Side} {From}-{To}";
}

/// <summary>
/// Tile-level reachability analysis of one room with an approximate Celeste movement model: standing, walking,
/// jump/fall arcs, one dash (Basic), wall climbing with stamina, wall jumps, swimming, springs/boosters/bumpers/
/// feathers as launch zones, spikes/spinners/lightning as lethal cells, jump-throughs as one-way platforms.
/// It answers "which exits, items and areas can the player reach from the spawn (or an entry), and what does it take":
/// the verifier that lets an agent check a room it just built. Envelopes are conservative approximations of
/// Celeste physics (jump ~ 3 tiles up / 6 across, dash ~ +4 tiles, stamina ~ 10 tiles of climbing), not a simulation.
/// </summary>
public sealed class RoomAnalysis
{
    public readonly int Cols, Rows;
    public readonly CellKind[,] Grid;
    public readonly List<RoomExit> Exits = new();
    public readonly List<(int X, int Y, string Kind, int Radius)> Launchers = new();
    public readonly List<(string Name, int Id, int X, int Y, bool Reachable)> Items = new();
    /// <summary>Moving blocks/platforms with nodes: standing on the block at its start carries the player to the top of its node positions.</summary>
    public readonly List<(string Name, List<(int X, int Y)> From, List<(int X, int Y)> To)> Rides = new();
    private readonly HashSet<int> _ridden = new();
    public (int X, int Y)? Spawn;
    public Tech Tech;
    public readonly List<string> Notes = new();

    private readonly bool[,] _ground, _air;
    private readonly (int X, int Y)[,] _origin;   // where the move that first touched a cell started (-1,-1 = a start)
    private readonly string?[,] _move;
    private readonly bool[,,] _wall;
    private readonly Queue<(int X, int Y, int Side)> _queue = new();
    private readonly HashSet<int> _launched = new();
    private Envelope _env = Envelope.For(Tech.Basic);

    public int GroundCount, AirCount, PassableCells, HazardCells, HazardNearRoute, LongestJump, LongestClimb, Dashes;

    private RoomAnalysis(int cols, int rows)
    {
        Cols = cols; Rows = rows;
        Grid = new CellKind[cols, rows];
        _ground = new bool[cols, rows];
        _air = new bool[cols, rows];
        _origin = new (int, int)[cols, rows];
        _move = new string?[cols, rows];
        _wall = new bool[cols, rows, 2];
    }

    // ---- movement envelopes ----

    private sealed record Envelope(Tech Tech, int MaxUp, int MaxClimb, int WallDx, bool Duck, bool Dash)
    {
        /// <summary>Farthest horizontal landing (in tiles) for a height difference dy (negative = landing higher).</summary>
        public int MaxDx(int dy) => Tech switch
        {
            Tech.NoDash => dy switch { < -3 => 0, -3 => 2, -2 => 4, -1 => 5, 0 or 1 => 6, >= 2 and <= 5 => 7, _ => 8 },
            Tech.Basic => dy switch { < -6 => 0, -6 => 2, -5 => 3, -4 => 4, -3 => 6, -2 => 8, -1 => 9, 0 or 1 => 10, >= 2 and <= 5 => 11, _ => 12 },
            _ => dy switch { < -7 => 0, -7 => 2, -6 => 3, -5 => 4, -4 => 6, -3 => 8, -2 => 11, -1 => 13, 0 => 14, >= 1 and <= 3 => 15, _ => 16 },
        };

        public int MaxDxAny => Tech switch { Tech.NoDash => 8, Tech.Basic => 12, _ => 16 };

        public static Envelope For(Tech t) => t switch
        {
            Tech.NoDash => new(t, 3, 10, 4, false, false),
            Tech.Basic => new(t, 6, 10, 7, false, true),
            _ => new(t, 7, 14, 10, true, true),
        };
    }

    // ---- building the cell grid from a room ----

    /// <summary>Classifies the room's tiles and entities into cells, finds border openings, spawn and launchers. No movement yet.</summary>
    public static RoomAnalysis Build(Room room)
    {
        var a = new RoomAnalysis(room.Cols, room.Rows);
        var fg = room.Grid(true);
        for (int y = 0; y < a.Rows; y++)
            for (int x = 0; x < a.Cols; x++)
                a.Grid[x, y] = fg[x, y] != '0' ? CellKind.Solid : CellKind.Air;

        var hazards = new List<RectangleF>();
        var solids = new List<RectangleF>();
        var platforms = new List<RectangleF>();
        var waters = new List<RectangleF>();
        var dreams = new List<RectangleF>();
        foreach (var e in room.EntitiesEl.Children)
        {
            if (MapModel.IsSpawn(e))
            {
                a.Spawn ??= (Math.Clamp((int)Math.Floor(e.Num("x") / 8f), 0, a.Cols - 1), Math.Clamp((int)Math.Floor((e.Num("y") - 1) / 8f), 0, a.Rows - 1));
                continue;
            }
            var box = MapTools.Local(room, MapModel.Box(room, e, false));
            string n = Short(e.Name);
            string? cat = ReferenceMaps.Category(e.Name);
            if (EntityVisuals.SpikeDir(e.Name) != null || cat is "spikes" or "spinners" or "other_hazards") { hazards.Add(box); continue; }
            if (n == "water" || n.EndsWith("/water")) { waters.Add(box); continue; }
            if (n.Contains("dreamblock")) { dreams.Add(box); continue; }
            if (n.Contains("spring")) { a.Launchers.Add((Cx(box), Cy(box), "spring", 7)); continue; }
            if (n.Contains("booster"))
            {
                bool red = e.Str("red").Equals("true", StringComparison.OrdinalIgnoreCase);
                a.Launchers.Add((Cx(box), Cy(box), red ? "red booster" : "booster", red ? 16 : 9));
                continue;
            }
            if (n.Contains("bumper") || n.Contains("bigspinner")) { a.Launchers.Add((Cx(box), Cy(box), "bumper", 7)); continue; }
            if (n.Contains("infinitestar") || n.Contains("feather")) { a.Launchers.Add((Cx(box), Cy(box), "feather", 40)); continue; }
            if (n.Contains("cloud")) { a.Launchers.Add((Cx(box), Cy(box), "cloud", 7)); continue; }
            bool sized = e.Num("width") > 0 && e.Num("height") > 0;
            var nodeBoxes = e.Children.Where(c => c.Name == "node")
                .Select(node => new RectangleF(box.X + node.Num("x") - e.Num("x"), box.Y + node.Num("y") - e.Num("y"), box.Width, box.Height)).ToList();
            if (n.Contains("jumpthru") || (n.Contains("platform") && !n.Contains("controller")))
            {
                platforms.Add(box);
                if (nodeBoxes.Count > 0 && n.Contains("platform")) { platforms.AddRange(nodeBoxes); a.AddRide(e.Name, box, nodeBoxes); }
                continue;
            }
            if (sized && IsSolidName(n))
            {
                solids.Add(box);
                if (nodeBoxes.Count > 0 && (n.Contains("zip") || n.Contains("swap") || n.Contains("tracked") || n.Contains("moving")))
                {
                    solids.AddRange(nodeBoxes);
                    a.AddRide(e.Name, box, nodeBoxes);
                }
                continue;
            }
            if (cat is "refills" or "collectibles" || n.Contains("key") || n.Contains("berry") || n.Contains("gem") || n.Contains("heart") || n.Contains("cassette") || n.Contains("refill"))
                a.Items.Add((e.Name, e.Int("id"), Cx(box), Cy(box), false));
        }
        foreach (var r in solids) a.Paint(r, CellKind.Solid);
        foreach (var r in platforms) a.Paint(r, CellKind.Platform, onlyAir: true);
        foreach (var r in dreams) a.Paint(r, CellKind.Dream);
        foreach (var r in waters) a.Paint(r, CellKind.Water, onlyAir: true);
        foreach (var r in hazards) a.Paint(r, CellKind.Hazard, onlyAir: true);

        for (int y = 0; y < a.Rows; y++)
            for (int x = 0; x < a.Cols; x++)
            {
                if (a.Grid[x, y] == CellKind.Hazard) a.HazardCells++;
                if (a.Grid[x, y] is CellKind.Air or CellKind.Water) a.PassableCells++;
            }
        a.FindExits();
        return a;
    }

    private static string Short(string name)
    {
        int slash = name.LastIndexOf('/');
        return (slash >= 0 ? name[(slash + 1)..] : name).ToLowerInvariant();
    }

    private static bool IsSolidName(string n)
    {
        if (n.Contains("fake") || n.Contains("trigger") || n.Contains("controller") || n.Contains("seekerbarrier") || n.Contains("switch") || n.Contains("spawner") || n.Contains("lightning"))
            return false;
        foreach (string w in new[] { "block", "wall", "barrier", "zipmover", "zip", "crush", "kevin", "crumble", "coverup", "glass", "cassette", "clutter", "temple", "crusher", "floaty", "starjump" })
            if (n.Contains(w)) return true;
        return false;
    }

    private static int Cx(RectangleF r) => (int)Math.Floor((r.X + r.Width / 2) / 8f);
    private static int Cy(RectangleF r) => (int)Math.Floor((r.Y + r.Height / 2) / 8f);

    /// <summary>The cells a player stands in on top of a block.</summary>
    private static List<(int X, int Y)> TopCells(RectangleF box)
    {
        int x0 = (int)Math.Floor((box.Left + 2) / 8f), x1 = (int)Math.Ceiling((box.Right - 2) / 8f) - 1, y = (int)Math.Floor((box.Top + 2) / 8f) - 1;
        var cells = new List<(int, int)>();
        for (int x = x0; x <= x1; x++) cells.Add((x, y));
        return cells;
    }

    private void AddRide(string name, RectangleF start, List<RectangleF> nodes)
    {
        var to = new List<(int X, int Y)>();
        foreach (var nb in nodes) to.AddRange(TopCells(nb));
        Rides.Add((name, TopCells(start), to));
    }

    /// <summary>Moving blocks the player has stood on carry them to their node positions; run once per ride.</summary>
    private bool TriggerRides()
    {
        bool any = false;
        for (int i = 0; i < Rides.Count; i++)
        {
            if (_ridden.Contains(i)) continue;
            var (name, from, to) = Rides[i];
            (int X, int Y) boarded = from.FirstOrDefault(c => ReachedGround(c.X, c.Y), (X: -1, Y: -1));
            if (boarded.X < 0) continue;
            _ridden.Add(i);
            // a block that travels sideways flings the player off with its momentum: a longer jump in the travel direction
            int travel = to.Count > 0 ? to[0].X - from[0].X : 0;
            int fling = Math.Abs(travel) >= 4 ? 6 : 0;
            foreach (var (x, y) in to)
            {
                if (!InBounds(x, y)) continue;
                MarkAir(x, y, boarded, "ride " + Short(name));
                if (CanStand(x, y))
                {
                    AddGround(x, y, boarded, "ride " + Short(name));
                    if (fling > 0)
                    {
                        int apex = 0;
                        while (apex < 3 && Pass(x, y - apex - 1) && y - apex - 1 >= -1) apex++;
                        Sweep(x, y, Math.Min(apex, y), Math.Sign(travel), _env.MaxDxAny, (x, y), "fling off " + Short(name), fling);
                    }
                }
                GrabIfWall(x, y, boarded, "ride " + Short(name));
                any = true;
            }
        }
        return any;
    }

    /// <summary>Marks the cells a box covers by at least 2px in each direction.</summary>
    private void Paint(RectangleF r, CellKind kind, bool onlyAir = false)
    {
        int x0 = (int)Math.Floor((r.Left + 2) / 8f), x1 = (int)Math.Ceiling((r.Right - 2) / 8f) - 1;
        int y0 = (int)Math.Floor((r.Top + 2) / 8f), y1 = (int)Math.Ceiling((r.Bottom - 2) / 8f) - 1;
        for (int y = Math.Max(0, y0); y <= Math.Min(Rows - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(Cols - 1, x1); x++)
                if (!onlyAir || Grid[x, y] != CellKind.Solid) Grid[x, y] = kind;
    }

    private void FindExits()
    {
        void Scan(string side, int len, Func<int, (int X, int Y)> cell)
        {
            int start = -1;
            for (int i = 0; i <= len; i++)
            {
                bool open = i < len && Grid[cell(i).X, cell(i).Y] is CellKind.Air or CellKind.Water;
                if (open && start < 0) start = i;
                else if (!open && start >= 0)
                {
                    Exits.Add(new RoomExit { Side = side, From = start, To = i - 1 });
                    start = -1;
                }
            }
        }
        Scan("left", Rows, i => (0, i));
        Scan("right", Rows, i => (Cols - 1, i));
        Scan("top", Cols, i => (i, 0));
        Scan("bottom", Cols, i => (i, Rows - 1));
        // A one-cell slit in a side wall is not an opening the player fits through.
        Exits.RemoveAll(e => e.Side is "left" or "right" && e.To == e.From);
    }

    // ---- analysis ----

    /// <summary>Runs the movement search from the spawn (or <paramref name="starts"/>) with a tech budget.</summary>
    public static RoomAnalysis Analyze(Room room, Tech tech, IEnumerable<(int X, int Y)>? starts = null)
    {
        var a = Build(room);
        a.Run(tech, starts);
        return a;
    }

    public void Run(Tech tech, IEnumerable<(int X, int Y)>? starts = null)
    {
        Tech = tech;
        _env = Envelope.For(tech);
        Array.Clear(_ground); Array.Clear(_air); Array.Clear(_wall); Array.Clear(_move);
        for (int y = 0; y < Rows; y++) for (int x = 0; x < Cols; x++) _origin[x, y] = (-1, -1);
        _launched.Clear(); _ridden.Clear(); _queue.Clear();
        GroundCount = AirCount = HazardNearRoute = LongestJump = LongestClimb = Dashes = 0;
        Notes.Clear();
        foreach (var e in Exits) e.Reached = false;

        var startList = (starts ?? (Spawn is { } s ? new[] { s } : Array.Empty<(int, int)>())).ToList();
        if (startList.Count == 0) Notes.Add("No player spawn in the room (and no entry given): nothing to analyze from.");
        foreach (var (sx, sy) in startList)
        {
            if (!InBounds(sx, sy)) { Notes.Add($"Start ({sx},{sy}) is outside the room."); continue; }
            if (!Pass(sx, sy)) { Notes.Add($"Start ({sx},{sy}) is inside a {Grid[sx, sy].ToString().ToLowerInvariant()} cell; the player would be stuck or die."); continue; }
            Drop(sx, sy, (-1, -1), "start");
        }

        while (true)
        {
            while (_queue.Count > 0)
            {
                var (x, y, side) = _queue.Dequeue();
                if (side == 0) ExpandGround(x, y);
                else ExpandWall(x, y, side);
            }
            bool more = TriggerLaunchers();
            more |= TriggerRides();
            if (!more) break;
        }

        foreach (var e in Exits)
            e.Reached = e.Cells(Cols, Rows).Any(c => _air[c.X, c.Y] || _ground[c.X, c.Y]);
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            bool r = false;
            for (int dy = -1; dy <= 1 && !r; dy++)
                for (int dx = -1; dx <= 1 && !r; dx++)
                    if (ReachedAir(it.X + dx, it.Y + dy)) r = true;
            Items[i] = (it.Name, it.Id, it.X, it.Y, r);
        }
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
            {
                if (_ground[x, y]) GroundCount++;
                else if (_air[x, y]) AirCount++;
                if (Grid[x, y] != CellKind.Hazard) continue;
                bool near = false;
                for (int dy = -1; dy <= 1 && !near; dy++)
                    for (int dx = -1; dx <= 1 && !near; dx++)
                        if (ReachedAir(x + dx, y + dy)) near = true;
                if (near) HazardNearRoute++;
            }
    }

    public bool ReachedGround(int x, int y) => InBounds(x, y) && _ground[x, y];
    public bool ReachedAir(int x, int y) => InBounds(x, y) && (_air[x, y] || _ground[x, y]);

    private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Cols && y < Rows;

    /// <summary>Can the player's body occupy this cell? Rows above the room count as open (the way out of the top).</summary>
    private bool Pass(int x, int y)
    {
        if (x < 0 || x >= Cols || y >= Rows) return false;
        if (y < 0) return true;
        return Grid[x, y] switch
        {
            CellKind.Air or CellKind.Water or CellKind.Platform => true,
            CellKind.Dream => _env.Dash,
            _ => false
        };
    }

    private bool IsWall(int x, int y) => InBounds(x, y) && Grid[x, y] is CellKind.Solid or CellKind.Dream;

    private bool Supports(int x, int y) => InBounds(x, y) && Grid[x, y] is CellKind.Solid or CellKind.Platform or CellKind.Dream;

    private bool CanStand(int x, int y)
    {
        if (!InBounds(x, y) || !Pass(x, y) || Grid[x, y] == CellKind.Platform) return false;
        if (Grid[x, y] == CellKind.Water) return true;
        if (!_env.Duck && !Pass(x, y - 1)) return false;
        return Supports(x, y + 1);
    }

    private void MarkAir(int x, int y, (int X, int Y) from, string move)
    {
        if (!InBounds(x, y) || _air[x, y] || _ground[x, y]) return;
        _air[x, y] = true;
        _origin[x, y] = from;
        _move[x, y] = move;
    }

    private void AddGround(int x, int y, (int X, int Y) from, string move)
    {
        if (!InBounds(x, y) || _ground[x, y]) return;
        _ground[x, y] = true;
        _origin[x, y] = from;
        _move[x, y] = move;
        _queue.Enqueue((x, y, 0));
    }

    private void AddWall(int x, int y, int side, (int X, int Y) from, string move)
    {
        int s = side < 0 ? 0 : 1;
        if (!InBounds(x, y) || _wall[x, y, s]) return;
        _wall[x, y, s] = true;
        MarkAir(x, y, from, move);
        _queue.Enqueue((x, y, side));
    }

    /// <summary>Falls straight down from an air cell until landing, dying or leaving through the bottom.</summary>
    private void Drop(int x, int y, (int X, int Y) from, string move)
    {
        for (int r = y; ; r++)
        {
            if (r >= Rows) return;
            if (!Pass(x, r)) return;
            if (CanStand(x, r)) { AddGround(x, r, from, move); return; }
            MarkAir(x, r, from, move);
            GrabIfWall(x, r, from, move);
        }
    }

    /// <summary>A wall is grabbable beside the body, or beside the head when the head cell itself is open (a ceiling is not a wall).</summary>
    private bool Grabbable(int x, int y, int side) =>
        IsWall(x + side, y) || (Pass(x, y - 1) && IsWall(x + side, y - 1));

    private void GrabIfWall(int x, int y, (int X, int Y) from, string move)
    {
        if (Grabbable(x, y, -1)) AddWall(x, y, -1, from, move + ", grab wall");
        if (Grabbable(x, y, +1)) AddWall(x, y, +1, from, move + ", grab wall");
    }

    private void ExpandGround(int x, int y)
    {
        var from = (x, y);
        int apexMax = 0;
        while (apexMax < _env.MaxUp && Pass(x, y - apexMax - 1) && y - apexMax - 1 >= -1) apexMax++;
        apexMax = Math.Min(apexMax, y); // the body row stays inside the room; the head may poke out of the top
        for (int a = 0; a <= apexMax; a++) MarkAir(x, y - a, from, "jump");
        Sweep(x, y, apexMax, -1, _env.MaxDxAny, from, "jump");
        Sweep(x, y, apexMax, +1, _env.MaxDxAny, from, "jump");

        // swimming: water is traversable in every direction
        if (Grid[x, y] == CellKind.Water)
            foreach (var (nx, ny) in new[] { (x, y - 1), (x, y + 1), (x - 1, y), (x + 1, y) })
                if (InBounds(nx, ny) && Grid[nx, ny] == CellKind.Water) AddGround(nx, ny, from, "swim");

        // a wall right beside the player: grab and climb
        GrabIfWall(x, y, from, "grab wall");
    }

    /// <summary>
    /// Jump/fall arcs as "rise to an apex row, travel sideways while the body fits, then fall": every column the player
    /// can drift into is descended to its first landing, and the landing is accepted when the horizontal distance is within
    /// the envelope for that height difference. Lower apexes are tried too, so the player can go under overhangs.
    /// </summary>
    private void Sweep(int x, int y, int apexMax, int dir, int maxDx, (int X, int Y) from, string kind, int bonusDx = 0)
    {
        for (int a = apexMax; a >= 0; a--)
        {
            int r0 = y - a;
            for (int dx = 1; dx <= maxDx + bonusDx; dx++)
            {
                int cx = x + dir * dx;
                if (cx < 0 || cx >= Cols) break;
                if (!Pass(cx, r0)) break;
                if (!_env.Duck && !Pass(cx, r0 - 1)) break;
                MarkAir(cx, r0, from, kind);
                for (int r = r0; ; r++)
                {
                    if (r >= Rows) break;
                    if (!Pass(cx, r)) break;
                    MarkAir(cx, r, from, kind);
                    GrabIfWall(cx, r, from, $"{kind} {Dir(dir)} {dx}");
                    if (CanStand(cx, r))
                    {
                        int dy = r - y;
                        if (dx <= _env.MaxDx(dy) + (dy >= -1 ? bonusDx : 0))
                        {
                            bool dash = _env.Dash && dx > Envelope.For(Tech.NoDash).MaxDx(dy);
                            if (!_ground[cx, r]) { LongestJump = Math.Max(LongestJump, dx); if (dash) Dashes++; }
                            AddGround(cx, r, from, $"{kind} {Dir(dir)} {dx}{(dy != 0 ? $" {(dy < 0 ? "up" : "down")} {Math.Abs(dy)}" : "")}{(dash ? " (dash)" : "")}");
                        }
                        break;
                    }
                }
            }
        }
    }

    private static string Dir(int d) => d < 0 ? "left" : "right";

    /// <summary>Climb the wall at <paramref name="side"/> from a grab cell: up while stamina and the wall last, onto its top, or jump away.</summary>
    private void ExpandWall(int x, int y, int side)
    {
        var from = (x, y);
        int wx = x + side;
        for (int k = 0; k <= _env.MaxClimb; k++)
        {
            int ry = y - k;
            if (ry < 0) break;
            if (k > 0 && !Pass(x, ry)) break;
            if (!Grabbable(x, ry, side))
            {
                // the wall ended below the hands: mantle onto its top
                if (CanStand(wx, ry)) { LongestClimb = Math.Max(LongestClimb, k); AddGround(wx, ry, from, $"climb {k} onto ledge"); }
                break;
            }
            MarkAir(x, ry, from, $"climb {k}");
            if (k > 0) LongestClimb = Math.Max(LongestClimb, k);
            if (IsWall(wx, ry) && !IsWall(wx, ry - 1) && CanStand(wx, ry - 1))
                AddGround(wx, ry - 1, from, $"climb {k} onto ledge");
            int apex = 0;
            while (apex < 2 && ry - apex - 1 >= 0 && Pass(x, ry - apex - 1)) apex++;
            Sweep(x, ry, apex, -side, _env.WallDx, from, "wall-jump");
        }
        Drop(x, y + 1, from, "drop");
    }

    /// <summary>Springs, boosters, bumpers and feathers the player has touched throw them into a zone; run once per launcher.</summary>
    private bool TriggerLaunchers()
    {
        bool any = false;
        for (int i = 0; i < Launchers.Count; i++)
        {
            if (_launched.Contains(i)) continue;
            var (lx, ly, kind, radius) = Launchers[i];
            bool touched = false;
            for (int dy = -1; dy <= 1 && !touched; dy++)
                for (int dx = -1; dx <= 1 && !touched; dx++)
                    if (ReachedAir(lx + dx, ly + dy)) touched = true;
            if (!touched) continue;
            _launched.Add(i);
            any = true;
            var from = (lx, ly);
            var seen = new HashSet<(int, int)> { (lx, ly) };
            var q = new Queue<(int X, int Y, int D)>();
            q.Enqueue((lx, ly, 0));
            while (q.Count > 0)
            {
                var (cx, cy, d) = q.Dequeue();
                if (InBounds(cx, cy))
                {
                    MarkAir(cx, cy, from, kind);
                    if (CanStand(cx, cy)) AddGround(cx, cy, from, kind);
                    GrabIfWall(cx, cy, from, kind);
                }
                if (d >= radius) continue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if ((dx == 0 && dy == 0) || !InBounds(nx, ny) || !Pass(nx, ny) || !seen.Add((nx, ny))) continue;
                        if (kind is "spring" or "cloud" && ny > ly + 1) continue; // springs only throw upward
                        q.Enqueue((nx, ny, d + 1));
                    }
            }
        }
        return any;
    }

    // ---- reporting ----

    /// <summary>The chain of moves that first reached a cell, oldest first.</summary>
    public List<string> RouteTo(int x, int y)
    {
        var steps = new List<string>();
        var seen = new HashSet<(int, int)>();
        var cur = (X: x, Y: y);
        while (InBounds(cur.X, cur.Y) && seen.Add(cur) && steps.Count < 60)
        {
            var o = _origin[cur.X, cur.Y];
            string m = _move[cur.X, cur.Y] ?? "";
            if (o.X < 0) { steps.Add($"start ({cur.X},{cur.Y})"); break; }
            steps.Add($"{m} -> ({cur.X},{cur.Y})");
            cur = o;
        }
        steps.Reverse();
        return steps;
    }

    /// <summary>Nearest reached cell to a cell, as (distance, cell).</summary>
    public (int Dist, int X, int Y)? Nearest(int tx, int ty)
    {
        (int Dist, int X, int Y)? best = null;
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
                if (_ground[x, y] || _air[x, y])
                {
                    int d = Math.Max(Math.Abs(x - tx), Math.Abs(y - ty));
                    if (best == null || d < best.Value.Dist) best = (d, x, y);
                }
        return best;
    }

    public double HazardPressure => GroundCount + AirCount > 0 ? HazardNearRoute / (double)(GroundCount + AirCount) : 0;

    public JsonObject ToJson(bool includeRoutes = true)
    {
        var o = new JsonObject
        {
            ["tech"] = Tech.ToString().ToLowerInvariant(),
            ["cols"] = Cols, ["rows"] = Rows,
            ["spawn"] = Spawn is { } s ? new JsonArray(s.X, s.Y) : null,
            ["reached"] = new JsonObject
            {
                ["standing_cells"] = GroundCount, ["air_cells"] = AirCount,
                ["coverage"] = PassableCells > 0 ? Math.Round((GroundCount + AirCount) / (double)PassableCells, 3) : 0,
                ["longest_jump_tiles"] = LongestJump, ["longest_climb_tiles"] = LongestClimb, ["dash_moves"] = Dashes
            },
            ["hazards"] = new JsonObject
            {
                ["cells"] = HazardCells, ["touching_route"] = HazardNearRoute, ["pressure"] = Math.Round(HazardPressure, 3)
            },
        };
        var exits = new JsonArray();
        foreach (var e in Exits)
        {
            var ej = new JsonObject { ["side"] = e.Side, ["from"] = e.From, ["to"] = e.To, ["reached"] = e.Reached };
            if (e.MinTech is { } mt) ej["min_tech"] = mt.ToString().ToLowerInvariant();
            else if (!e.Reached) ej["min_tech"] = "unreachable";
            if (!e.Reached && (GroundCount + AirCount) > 0)
            {
                var mid = e.Cells(Cols, Rows).Skip((e.To - e.From) / 2).First();
                if (Nearest(mid.X, mid.Y) is { } n) ej["closest_reached"] = $"({n.X},{n.Y}): {Math.Abs(mid.X - n.X)} tiles across, {Math.Abs(mid.Y - n.Y)} tiles up/down from the exit";
            }
            if (includeRoutes && e.Reached)
            {
                var c = e.Cells(Cols, Rows).First(c => _air[c.X, c.Y] || _ground[c.X, c.Y]);
                ej["route"] = string.Join("; ", RouteTo(c.X, c.Y));
            }
            exits.Add(ej);
        }
        o["exits"] = exits;
        if (Items.Count > 0)
        {
            o["items"] = new JsonObject
            {
                ["reachable"] = Items.Count(i => i.Reachable),
                ["unreachable"] = new JsonArray(Items.Where(i => !i.Reachable).Take(30).Select(i => (JsonNode)$"{i.Name} #{i.Id} at cell ({i.X},{i.Y})").ToArray())
            };
        }
        if (Launchers.Count > 0) o["launchers"] = string.Join(", ", Launchers.Select((l, i) => $"{l.Kind} ({l.X},{l.Y}){(_launched.Contains(i) ? "" : " untouched")}"));
        if (Rides.Count > 0) o["rides"] = string.Join(", ", Rides.Select((r, i) => $"{Short(r.Name)} from ({r.From[0].X},{r.From[0].Y}){(_ridden.Contains(i) ? " ridden" : " not boarded")}"));
        if (Notes.Count > 0) o["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray());
        return o;
    }

    /// <summary>
    /// Full report: the Basic analysis plus the minimum tech each exit needs (none / basic / advanced / unreachable) and
    /// plain-language hints about what blocks the player.
    /// </summary>
    public static (RoomAnalysis Basic, JsonObject Report) Report(Room room, IEnumerable<(int X, int Y)>? starts = null)
    {
        var startList = starts?.ToList();
        var no = Analyze(room, Tech.NoDash, startList);
        var basic = Analyze(room, Tech.Basic, startList);
        var adv = Analyze(room, Tech.Advanced, startList);
        for (int i = 0; i < basic.Exits.Count; i++)
            basic.Exits[i].MinTech = no.Exits[i].Reached ? Tech.NoDash : basic.Exits[i].Reached ? Tech.Basic : adv.Exits[i].Reached ? Tech.Advanced : null;
        var json = basic.ToJson();
        var hints = new List<string>();
        if (basic.Spawn == null && startList == null) hints.Add("Add a player spawn (add_entity name='player') before judging the room.");
        if (basic.Exits.Count == 0) hints.Add("The room has no openings in its border: carve exits (draw_tiles op 'exit' or connect_rooms).");
        foreach (var e in basic.Exits)
            hints.Add(e.MinTech switch
            {
                Tech.NoDash => $"Exit {e.Label}: reachable without a dash.",
                Tech.Basic => $"Exit {e.Label}: needs a dash.",
                Tech.Advanced => $"Exit {e.Label}: only with advanced tech (hyper/wavedash/demo or a long climb): fine for expert+, too hard below.",
                _ => $"Exit {e.Label}: UNREACHABLE even with advanced tech; shorten the gap, add a platform/ledge or a spring/booster."
            });
        if (basic.Items.Any(i => !i.Reachable)) hints.Add($"{basic.Items.Count(i => !i.Reachable)} item(s) can't be reached (see items.unreachable).");
        hints.Add(basic.HazardPressure switch
        {
            < 0.03 => "Hazard pressure is very low (relaxed / beginner feel).",
            < 0.08 => "Hazard pressure is light (beginner to intermediate).",
            < 0.16 => "Hazard pressure is moderate (intermediate to advanced).",
            < 0.28 => "Hazard pressure is high (advanced to expert).",
            _ => "Hazard pressure is extreme (expert to grandmaster)."
        });
        if (basic.PassableCells > 0 && (basic.GroundCount + basic.AirCount) / (double)basic.PassableCells < 0.35 && basic.Exits.Any(e => e.Reached))
            hints.Add("Less than a third of the open space is reachable: large unused pockets. Fill them, use them, or shrink the room.");
        json["hints"] = new JsonArray(hints.Select(h => (JsonNode)h).ToArray());
        json["required_tech"] = basic.Exits.Count == 0 ? "n/a"
            : basic.Exits.All(e => e.MinTech == Tech.NoDash) ? "none"
            : basic.Exits.All(e => e.MinTech is Tech.NoDash or Tech.Basic) ? "dash"
            : basic.Exits.All(e => e.MinTech != null) ? "advanced" : "unreachable exits";
        return (basic, json);
    }

    /// <summary>Color per cell for the overlay renderer (ARGB), or 0 for nothing.</summary>
    public int OverlayColor(int x, int y)
    {
        if (Grid[x, y] == CellKind.Hazard) return unchecked((int)0x70FF3030);
        if (_ground[x, y]) return unchecked((int)0x6030FF60);
        if (_air[x, y]) return unchecked((int)0x3040C0FF);
        return 0;
    }
}
