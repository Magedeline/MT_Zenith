namespace CELMapViewer;

/// <summary>
/// Room size tiers (normal … xxl) and directional resize/enlarge, shared by the GUI and the MCP/ACP tools.
/// Tiers follow the room-size spread of vanilla Celeste, CEL's A-Sides, Strawberry Jam and Spring Collab:
/// about a third of rooms are one screen, the median is ~1.3 screens wide, and the biggest set-piece rooms
/// reach 20+ screens (long corridors up to ~100 screens wide, climbs up to ~40 screens tall).
/// </summary>
public static class RoomSizing
{
    public const int ScreenW = 320, ScreenH = 184;

    /// <summary>Size tiers and their area in screens.</summary>
    public static readonly (string Name, int Screens, string Use)[] Tiers =
    {
        ("normal", 1, "single screen (vanilla default, ~1/3 of collab rooms)"),
        ("medium", 2, "two screens, the collab median"),
        ("large", 4, "multi-screen challenge room"),
        ("xl", 9, "big exploration/gimmick room"),
        ("xxl", 20, "set-piece, chase, boss arena or long corridor/climb"),
    };

    public static readonly string[] Shapes = { "auto", "wide", "square", "tall" };

    /// <summary>Anchors name the part of the room that stays put; the room grows away from it ("center" grows every direction).</summary>
    public static readonly string[] Anchors = { "top-left", "top", "top-right", "left", "center", "right", "bottom-left", "bottom", "bottom-right" };

    public enum Fill { Extend, Empty, Tile }

    /// <summary>Pixel size of a tier in a shape ("auto" keeps the room's orientation).</summary>
    public static Size TierSize(string tier, string shape, Size? current = null)
    {
        int n = Tiers.FirstOrDefault(t => t.Name.Equals(tier, StringComparison.OrdinalIgnoreCase)).Screens;
        if (n == 0) throw new ArgumentException($"Unknown size '{tier}'. Use one of: {string.Join(", ", Tiers.Select(t => t.Name))}.");
        shape = (shape ?? "auto").ToLowerInvariant();
        if (shape == "auto")
            shape = current is { } c && c.Height > c.Width * ScreenH / ScreenW * 1.4 ? "tall"
                  : current is { } d && d.Width * ScreenH / ScreenW > d.Height * 1.4 ? "wide" : n <= 2 ? "wide" : "square";
        int sw, sh;
        switch (shape)
        {
            case "wide": sw = n <= 4 ? n : (n + 1) / 2; sh = n <= 4 ? 1 : 2; break;
            case "tall": sh = n <= 4 ? n : (n + 1) / 2; sw = n <= 4 ? 1 : 2; break;
            case "square": sw = (int)Math.Ceiling(Math.Sqrt(n)); sh = (int)Math.Ceiling(n / (double)sw); break;
            default: throw new ArgumentException($"Unknown shape '{shape}'. Use auto, wide, square or tall.");
        }
        return new Size(sw * ScreenW, sh * ScreenH);
    }

    /// <summary>The tier a room size falls into (by area in screens).</summary>
    public static string TierOf(int w, int h)
    {
        double s = w * (double)h / (ScreenW * 180.0);
        return s <= 1.1 ? "normal" : s <= 2.2 ? "medium" : s <= 4.5 ? "large" : s <= 9.5 ? "xl" : "xxl";
    }

    /// <summary>Edge deltas (grow &gt; 0, shrink &lt; 0; multiples of 8) that turn <paramref name="b"/> into a <paramref name="size"/> room held at <paramref name="anchor"/>.</summary>
    public static (int Left, int Top, int Right, int Bottom) DeltasFor(Rectangle b, Size size, string anchor)
    {
        anchor = (anchor ?? "top-left").ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        if (!Anchors.Contains(anchor)) throw new ArgumentException($"Unknown anchor '{anchor}'. Use one of: {string.Join(", ", Anchors)}.");
        int dw = MapModel.Snap8(size.Width) - b.Width, dh = MapModel.Snap8(size.Height) - b.Height;
        (int l, int r) = anchor.Contains("left") ? (0, dw) : anchor.Contains("right") ? (dw, 0) : (Snap8Down(dw / 2), dw - Snap8Down(dw / 2));
        (int t, int bt) = anchor.Contains("top") ? (0, dh) : anchor.Contains("bottom") ? (dh, 0) : (Snap8Down(dh / 2), dh - Snap8Down(dh / 2));
        return (l, t, r, bt);
    }

    private static int Snap8Down(int v) => (int)Math.Floor(v / 8.0) * 8;

    /// <summary>Result of <see cref="Resize"/>: new bounds plus the rooms that were pushed or now overlap.</summary>
    public sealed record Result(Rectangle Old, Rectangle New, List<string> Pushed, List<string> Overlaps);

    /// <summary>
    /// Moves each edge of the room by the given pixels (positive grows outward, negative shrinks; snapped to 8) keeping
    /// content fixed in the world. New space is filled by extending the edge tiles (keeps floors, ceilings and walls closed),
    /// left empty, or filled with one fg tile. With <paramref name="pushRooms"/> the rooms beyond a growing edge are shifted
    /// by the same amount, like inserting space into the map, so nothing ends up overlapping.
    /// </summary>
    public static Result Resize(MapBin map, Room room, int left, int top, int right, int bottom, Fill fill = Fill.Extend, char tile = '1', bool pushRooms = false)
    {
        left = MapModel.Snap8(left); top = MapModel.Snap8(top); right = MapModel.Snap8(right); bottom = MapModel.Snap8(bottom);
        var old = room.Bounds;
        var nb = Rectangle.FromLTRB(old.Left - left, old.Top - top, old.Right + right, old.Bottom + bottom);
        if (nb.Width < 8 || nb.Height < 8) throw new ArgumentException($"The room would be {nb.Width}x{nb.Height}; it must stay at least 8x8.");
        if (nb.Width > 40000 || nb.Height > 40000) throw new ArgumentException($"{nb.Width}x{nb.Height} is too large (max 40000px per side).");

        var others = MapModel.Levels(map).Children.Where(l => l != room.El).Select(l => new Room(l)).ToList();
        var pushed = new List<string>();
        if (pushRooms)
            foreach (var o in others)
            {
                var ob = o.Bounds;
                int dx = (right > 0 && ob.Left >= old.Right ? right : 0) - (left > 0 && ob.Right <= old.Left ? left : 0);
                int dy = (bottom > 0 && ob.Top >= old.Bottom ? bottom : 0) - (top > 0 && ob.Bottom <= old.Top ? top : 0);
                if (dx == 0 && dy == 0) continue;
                o.El.Set("x", ob.X + dx);
                o.El.Set("y", ob.Y + dy);
                pushed.Add(o.Name);
            }

        MapModel.ResizeRoom(room, nb);
        if (fill != Fill.Empty)
        {
            // the part of the old room that is still inside, in new-grid cells
            var kept = Rectangle.Intersect(old, nb);
            int kx = (kept.X - nb.X) / 8, ky = (kept.Y - nb.Y) / 8, kw = kept.Width / 8, kh = kept.Height / 8;
            if (kw > 0 && kh > 0 && kept != nb)
            {
                FillNew(room.Grid(true), kx, ky, kw, kh, fill, tile);
                if (fill == Fill.Extend) FillNew(room.Grid(false), kx, ky, kw, kh, fill, tile);
                room.CommitGrid(true);
                room.CommitGrid(false);
            }
        }

        var overlaps = others.Where(o => o.Bounds.IntersectsWith(room.Bounds)).Select(o => o.Name).ToList();
        return new Result(old, room.Bounds, pushed, overlaps);
    }

    /// <summary>Fills the cells outside the kept rectangle: extend copies the nearest kept edge cell, tile uses one char.</summary>
    private static void FillNew(TileGrid g, int kx, int ky, int kw, int kh, Fill fill, char tile)
    {
        for (int y = 0; y < g.Rows; y++)
            for (int x = 0; x < g.Cols; x++)
            {
                if (x >= kx && x < kx + kw && y >= ky && y < ky + kh) continue;
                g[x, y] = fill == Fill.Tile ? tile : g[Math.Clamp(x, kx, kx + kw - 1), Math.Clamp(y, ky, ky + kh - 1)];
            }
    }

    /// <summary>Parses a fill argument: "extend", "empty"/"none", "solid" (tile '1') or a single tileset char.</summary>
    public static (Fill Mode, char Tile) ParseFill(string? s) => (s ?? "extend").Trim().ToLowerInvariant() switch
    {
        "" or "extend" => (Fill.Extend, '1'),
        "empty" or "none" or "0" => (Fill.Empty, '0'),
        "solid" => (Fill.Tile, '1'),
        { Length: 1 } c => (Fill.Tile, s!.Trim()[0]),
        var other => throw new ArgumentException($"Unknown fill '{other}'. Use extend, empty, solid or a tileset char.")
    };
}
