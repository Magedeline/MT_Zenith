namespace CELMapViewer;

/// <summary>
/// The map's "Filler" element: solid rectangles outside rooms (children "rect" with x, y, w, h in tiles, world space).
/// The game fills them with solid tiles so the gaps between rooms look closed off.
/// </summary>
public static class Fillers
{
    public static BinElement Element(MapBin map)
    {
        var f = map.Root.Child("Filler");
        if (f == null) { f = new BinElement("Filler"); map.Root.Children.Add(f); }
        return f;
    }

    /// <summary>Filler rectangles in world pixels.</summary>
    public static List<Rectangle> Rects(MapBin map) =>
        (map.Root.Child("Filler")?.Children ?? new()).Where(r => r.Name == "rect")
            .Select(r => new Rectangle(r.Int("x") * 8, r.Int("y") * 8, r.Int("w") * 8, r.Int("h") * 8)).ToList();

    /// <summary>
    /// Splits a room's foreground tiles into as few rectangles as a greedy row merge finds
    /// (each run of solid tiles on a row grows downwards while the rows below match).
    /// Rectangles are in world tiles.
    /// </summary>
    public static List<Rectangle> TileRects(Room room)
    {
        var grid = room.Grid(true);
        var used = new bool[grid.Cols, grid.Rows];
        var rects = new List<Rectangle>();
        int ox = (int)Math.Floor(room.X / 8.0), oy = (int)Math.Floor(room.Y / 8.0);
        for (int y = 0; y < grid.Rows; y++)
            for (int x = 0; x < grid.Cols; x++)
            {
                if (used[x, y] || grid[x, y] == '0') continue;
                int w = 1;
                while (x + w < grid.Cols && !used[x + w, y] && grid[x + w, y] != '0') w++;
                int h = 1;
                while (y + h < grid.Rows && Enumerable.Range(x, w).All(i => !used[i, y + h] && grid[i, y + h] != '0')) h++;
                for (int j = y; j < y + h; j++) for (int i = x; i < x + w; i++) used[i, j] = true;
                rects.Add(new Rectangle(ox + x, oy + y, w, h));
            }
        return rects;
    }

    /// <summary>Turns a room's foreground tiles into filler rectangles and removes the room. Returns how many rectangles were added.</summary>
    public static int RoomToFiller(MapBin map, Room room)
    {
        var rects = TileRects(room);
        var filler = Element(map);
        foreach (var r in rects)
        {
            var el = new BinElement("rect");
            el.Set("x", r.X); el.Set("y", r.Y); el.Set("w", r.Width); el.Set("h", r.Height);
            filler.Children.Add(el);
        }
        MapModel.Levels(map).Children.Remove(room.El);
        return rects.Count;
    }

    /// <summary>
    /// Turns every filler rectangle into a room named "_filler_x_y" filled with <paramref name="tile"/>
    /// (or the tile just above / left / right / below it in a neighbouring room, like Lönn's script), and clears the fillers.
    /// </summary>
    public static List<BinElement> FillersToRooms(MapBin map, char tile = '1')
    {
        var levels = MapModel.Levels(map);
        var rooms = levels.Children.Select(l => new Room(l)).ToList();
        char Neighbour(int tx, int ty)
        {
            foreach (var r in rooms)
            {
                int x = tx - r.X / 8, y = ty - r.Y / 8;
                if (x >= 0 && y >= 0 && x < r.Cols && y < r.Rows && r.Grid(true)[x, y] is var c && c != '0') return c;
            }
            return '0';
        }
        var added = new List<BinElement>();
        var filler = map.Root.Child("Filler");
        foreach (var rect in filler?.Children.Where(c => c.Name == "rect").ToList() ?? new())
        {
            int x = rect.Int("x"), y = rect.Int("y"), w = Math.Max(1, rect.Int("w")), h = Math.Max(1, rect.Int("h"));
            char m = Neighbour(x, y - 1) is var a && a != '0' ? a : Neighbour(x - 1, y) is var b && b != '0' ? b
                   : Neighbour(x + w, y) is var c && c != '0' ? c : Neighbour(x, y + h) is var d && d != '0' ? d : tile;
            var grid = new TileGrid(w, h, m);
            string name = MapModel.UniqueName(map, $"_filler_{(x < 0 ? "n" : "")}{Math.Abs(x)}_{(y < 0 ? "n" : "")}{Math.Abs(y)}");
            var level = MapModel.NewLevel(name, x * 8, y * 8, w * 8, h * 8, grid.Serialize());
            levels.Children.Add(level);
            added.Add(level);
            filler!.Children.Remove(rect);
        }
        return added;
    }
}
