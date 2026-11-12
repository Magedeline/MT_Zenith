using System.Globalization;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace CELMapViewer;

/// <summary>A Lönn script (Loenn/scripts/*.lua) found in an installed mod.</summary>
public sealed class LoennScriptInfo
{
    public required string Name;
    public required string DisplayName;
    public required string Mod;
    public required string Rel;
    public required string Code;
    public string? Tooltip;
    public bool HasPrerun, HasRun;
    public override string ToString() => $"{DisplayName} [{Mod}]";
}

/// <summary>One script parameter for the arguments dialog.</summary>
public sealed class ScriptParam
{
    public required string Key;
    public object Value = "";
    public List<string> Options = new();
    public bool Editable = true;
    public string? Tooltip;
}

/// <summary>
/// Runs third-party Lönn scripts against the open map. Lönn's script API (rooms as tables with entities, triggers,
/// decals and tile matrices; <c>loaded_state</c>, <c>utils</c>, <c>structs.*</c>, <c>utils.matrix</c>, <c>entities</c>…)
/// is emulated in Lua on top of MoonSharp, and the changed tables are written back into the map.
/// Scripts that need Lönn's UI or its "$()" syntax extension fail with an error in the log.
/// </summary>
public static class LoennScripts
{
    private static List<LoennScriptInfo>? _cache;

    public static void Forget() => _cache = null;

    public static List<LoennScriptInfo> Discover()
    {
        if (_cache != null) return _cache;
        var lang = LangKeys();
        var list = new List<LoennScriptInfo>();
        foreach (var (src, rel, read) in GameAssets.EnumerateModFiles("Loenn/scripts", ".lua"))
        {
            if (read() is not { } bytes) continue;
            string code = System.Text.Encoding.UTF8.GetString(bytes);
            string mod = ModLabel(src);
            string fallback = Path.GetFileNameWithoutExtension(rel);
            var info = new LoennScriptInfo { Name = fallback, DisplayName = fallback, Mod = mod, Rel = rel, Code = code };
            try
            {
                var script = NewScript(null, null, _ => { });
                var t = LoadTable(script, code, rel);
                if (t == null) continue;   // a helper module, not a script
                if (t.Get("name").Type == DataType.String) info.Name = t.Get("name").String;
                info.DisplayName = t.Get("displayName").Type == DataType.String ? t.Get("displayName").String
                    : lang.GetValueOrDefault($"scripts.{info.Name}.name") ?? lang.GetValueOrDefault($"scripts.{info.Name}.displayName") ?? Spaced(info.Name);
                info.Tooltip = t.Get("tooltip").Type == DataType.String ? t.Get("tooltip").String : lang.GetValueOrDefault($"scripts.{info.Name}.tooltip");
                info.HasRun = t.Get("run").Type == DataType.Function;
                info.HasPrerun = t.Get("prerun").Type == DataType.Function;
                if (!info.HasRun && !info.HasPrerun) continue;
            }
            catch (Exception e) when (e is InterpreterException or InvalidOperationException or TimeoutException)
            {
                info.Tooltip = "Could not load: " + Short(e);
            }
            list.Add(info);
        }
        return _cache = list.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Mod).ToList();
    }

    private static Dictionary<string, string> LangKeys()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, rel, read) in GameAssets.EnumerateModFiles("Loenn/lang", ".lang"))
        {
            if (!rel.EndsWith("en_gb.lang", StringComparison.OrdinalIgnoreCase) || read() is not { } b) continue;
            foreach (string line in System.Text.Encoding.UTF8.GetString(b).Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && line.StartsWith("scripts.", StringComparison.Ordinal)) d.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
            }
        }
        return d;
    }

    private static string ModLabel(string src)
    {
        string? path = GameAssets.SourceByName(src);
        string name = (path != null ? GameAssets.EverestName(path) : null) ?? Path.GetFileNameWithoutExtension(src);
        return Spaced(name);
    }

    /// <summary>"LoennScripts" → "Loenn Scripts", "kyfex_randomize" → "kyfex randomize".</summary>
    public static string Spaced(string s) =>
        Regex.Replace(s.Replace('_', ' '), @"(?<=[a-z])(?=[A-Z])", " ").Trim();

    private static string Short(Exception e) => e is InterpreterException ie ? ie.DecoratedMessage ?? ie.Message : e.Message;

    // ---- parameters ----

    /// <summary>The script's parameters (defaults, field order, static options, tooltips).</summary>
    public static List<ScriptParam> Parameters(Table script)
    {
        var result = new List<ScriptParam>();
        if (script.Get("parameters") is { Type: DataType.Table } pv)
        {
            var info = script.Get("fieldInformation").Type == DataType.Table ? script.Get("fieldInformation").Table : null;
            var tips = script.Get("tooltips").Type == DataType.Table ? script.Get("tooltips").Table : null;
            var keys = new List<string>();
            if (script.Get("fieldOrder") is { Type: DataType.Table } fo)
                for (int i = 1; i <= fo.Table.Length; i++) if (fo.Table.Get(i).Type == DataType.String) keys.Add(fo.Table.Get(i).String);
            foreach (var p in pv.Table.Pairs.Where(p => p.Key.Type == DataType.String).OrderBy(p => p.Key.String, StringComparer.Ordinal))
                if (!keys.Contains(p.Key.String)) keys.Add(p.Key.String);
            foreach (string k in keys)
            {
                var v = pv.Table.Get(k);
                if (v.Type is not (DataType.Boolean or DataType.Number or DataType.String)) continue;
                var sp = new ScriptParam { Key = k, Value = FromLua(v, null)!, Tooltip = tips?.Get(k).Type == DataType.String ? tips.Get(k).String : null };
                if (info?.Get(k) is { Type: DataType.Table } fi)
                {
                    if (fi.Table.Get("options") is { Type: DataType.Table } opts)
                        foreach (var o in opts.Table.Pairs)
                        {
                            var ov = o.Value.Type == DataType.Table ? o.Value.Table.Get(2) : o.Value;
                            if (ov.Type is DataType.String or DataType.Number) sp.Options.Add(ov.ToPrintString());
                            else if (o.Key.Type == DataType.String) sp.Options.Add(o.Key.String);
                        }
                    if (fi.Table.Get("editable").Type == DataType.Boolean) sp.Editable = fi.Table.Get("editable").Boolean;
                }
                result.Add(sp);
            }
        }
        return result;
    }

    /// <summary>Reads a script's parameter list without running it on the map.</summary>
    public static List<ScriptParam> Parameters(LoennScriptInfo info)
    {
        try
        {
            var s = NewScript(null, null, _ => { });
            return LoadTable(s, info.Code, info.Rel) is { } t ? Parameters(t) : new();
        }
        catch (Exception e) when (e is InterpreterException or TimeoutException) { return new(); }
    }

    // ---- running ----

    public sealed class RunResult
    {
        public int RoomsChanged, RoomsAdded, RoomsRemoved;
        public bool FillersChanged;
        public List<string> Output = new();
    }

    /// <summary>
    /// Runs a script: <c>prerun(args, mode, ctx)</c> once, then <c>run(room, args, ctx)</c> for the current room or every room
    /// (Lönn's "Current Room" / "All Rooms"). <paramref name="askArgs"/> shows the arguments dialog and returns null to cancel;
    /// it is asked again if prerun hands over another script through <c>scriptsTool.useScript</c>.
    /// Returns null when cancelled.
    /// </summary>
    public static RunResult? Run(LoennScriptInfo info, MapBin map, BinElement? currentLevel, bool allRooms,
        Func<string, List<ScriptParam>, Dictionary<string, object>?> askArgs)
    {
        var result = new RunResult();
        var levels = MapModel.Levels(map).Children.ToList();
        var bridge = new Bridge(map, levels);
        var script = NewScript(map, bridge, s => result.Output.Add(s));
        var t = LoadTable(script, info.Code, info.Rel) ?? throw new InvalidOperationException("The script file did not return a script table.");

        var mapTable = bridge.BuildMap(script);
        script.Globals.Get("__state").Table["map"] = mapTable;
        var roomTables = mapTable.Get("rooms").Table;
        Table? current = null;
        for (int i = 1; i <= roomTables.Length; i++)
            if (roomTables.Get(i).Table.Get("__mtz").Number - 1 is var idx && idx >= 0 && idx < levels.Count && levels[(int)idx] == currentLevel)
                current = roomTables.Get(i).Table;
        script.Globals.Get("__state").Table["__current"] = current != null ? DynValue.NewTable(current) : DynValue.Nil;

        var args = askArgs(info.DisplayName, Parameters(t));
        if (args == null) return null;
        var argTable = ToTable(script, args);
        var ctx = new Table(script);
        string mode = allRooms ? "allRooms" : "currentRoom";
        t["scriptsTool"] = script.Globals.Get("__scriptsTool");

        var handler = t;
        if (t.Get("prerun").Type == DataType.Function)
        {
            Call(script, t.Get("prerun"), DynValue.NewTable(argTable), DynValue.NewString(mode), DynValue.NewTable(ctx));
            if (script.Globals.Get("__pending") is { Type: DataType.Table } pending)
            {
                handler = pending.Table;
                var more = askArgs(handler.Get("displayName").Type == DataType.String ? handler.Get("displayName").String : info.DisplayName, Parameters(handler));
                if (more == null) return null;
                argTable = ToTable(script, more);
            }
        }
        if (handler.Get("run").Type == DataType.Function)
        {
            var targets = new List<Table>();
            if (allRooms) for (int i = 1; i <= roomTables.Length; i++) targets.Add(roomTables.Get(i).Table);
            else if (current != null) targets.Add(current);
            else if (handler == t && t.Get("prerun").Type != DataType.Function) throw new InvalidOperationException("Select a room first (or run it on all rooms).");
            foreach (var room in targets)
                Call(script, handler.Get("run"), DynValue.NewTable(room), DynValue.NewTable(argTable), DynValue.NewTable(ctx));
        }

        bridge.WriteBack(script, script.Globals.Get("__state").Table.Get("map").Table, result);
        return result;
    }

    private static Table ToTable(Script s, Dictionary<string, object> values)
    {
        var t = new Table(s);
        foreach (var (k, v) in values) t[k] = ToLua(v);
        return t;
    }

    private static DynValue Call(Script script, DynValue fn, params DynValue[] args)
    {
        var co = script.CreateCoroutine(fn).Coroutine;
        co.AutoYieldCounter = 500_000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = co.Resume(args);
        while (co.State == CoroutineState.ForceSuspended)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("The script ran for more than a minute and was stopped.");
            res = co.Resume();
        }
        return res;
    }

    private static Table? LoadTable(Script script, string code, string rel)
    {
        var fn = script.LoadString(code, null, rel);
        var res = Call(script, fn);
        if (res.Type == DataType.Tuple) res = res.Tuple.Length > 0 ? res.Tuple[0] : DynValue.Nil;
        return res.Type == DataType.Table ? res.Table : null;
    }

    private static Script NewScript(MapBin? map, Bridge? bridge, Action<string> print)
    {
        var script = new Script(CoreModules.Preset_SoftSandbox);
        script.Options.DebugPrint = print;
        script.Globals["__entityHandler"] = DynValue.NewCallback((c, a) =>
        {
            if (a.Count < 1 || a[0].Type != DataType.String) return DynValue.Nil;
            var def = PluginCatalog.Entity(a[0].String) ?? PluginCatalog.Trigger(a[0].String);
            if (def == null) return DynValue.Nil;
            var s = c.GetScript();
            var placements = new Table(s);
            foreach (var p in def.Placements)
            {
                var data = new Table(s);
                foreach (var (k, v) in p.Data) data[k] = ToLua(v);
                var pt = new Table(s) { ["name"] = p.Name.Length > 0 ? p.Name : "default", ["data"] = data };
                placements.Append(DynValue.NewTable(pt));
                if (placements.Get("default").IsNil()) placements["default"] = pt;
            }
            return DynValue.NewTable(new Table(s) { ["name"] = def.Name, ["placements"] = placements, ["fieldInformation"] = new Table(s) });
        });
        script.DoString(Prelude, null, "loenn_api");
        return script;
    }

    // ---- C# <-> Lua values ----

    public static DynValue ToLua(object? v) => v switch
    {
        null => DynValue.Nil,
        bool b => DynValue.NewBoolean(b),
        string s => DynValue.NewString(s),
        IConvertible c when v is not char => DynValue.NewNumber(c.ToDouble(CultureInfo.InvariantCulture)),
        _ => DynValue.NewString(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "")
    };

    /// <summary>Lua value → map value, keeping the original attribute's int/float kind.</summary>
    public static object? FromLua(DynValue v, BinAttr? orig) => v.Type switch
    {
        DataType.Boolean => v.Boolean,
        DataType.String => v.String,
        DataType.Number when orig?.Value is float or double => (float)v.Number,
        DataType.Number when v.Number == Math.Floor(v.Number) && Math.Abs(v.Number) < int.MaxValue => (int)v.Number,
        DataType.Number => (float)v.Number,
        _ => null
    };

    // ---- map <-> Lua tables ----

    private sealed class Bridge
    {
        private readonly MapBin _map;
        private readonly List<BinElement> _levels;
        private static readonly HashSet<string> RoomSpecial = new(StringComparer.Ordinal)
        { "entities", "triggers", "decalsFg", "decalsBg", "tilesFg", "tilesBg", "name", "x", "y", "width", "height", "color", "_type" };

        public Bridge(MapBin map, List<BinElement> levels) { _map = map; _levels = levels; }

        public Table BuildMap(Script s)
        {
            var map = new Table(s);
            var rooms = new Table(s);
            var mk = s.Globals.Get("__mkTiles");
            for (int i = 0; i < _levels.Count; i++)
            {
                var l = _levels[i];
                var room = new Room(l);
                var t = new Table(s) { ["_type"] = "room", ["__mtz"] = i + 1 };
                foreach (var a in l.Attrs)
                    if (a.Key == "c") t["color"] = ToLua(a.Value);
                    else if (a.Key != "color") t[a.Key] = ToLua(a.Value);
                t["entities"] = Items(s, l.Child("entities"), "entity");
                t["triggers"] = Items(s, l.Child("triggers"), "trigger");
                t["decalsFg"] = Decals(s, l.Child("fgdecals"));
                t["decalsBg"] = Decals(s, l.Child("bgdecals"));
                t["tilesFg"] = s.Call(mk, room.Grid(true).Serialize(), room.Cols, room.Rows);
                t["tilesBg"] = s.Call(mk, room.Grid(false).Serialize(), room.Cols, room.Rows);
                rooms.Append(DynValue.NewTable(t));
            }
            map["rooms"] = rooms;
            var fillers = new Table(s);
            foreach (var r in _map.Root.Child("Filler")?.Children.Where(c => c.Name == "rect") ?? Enumerable.Empty<BinElement>())
                fillers.Append(DynValue.NewTable(new Table(s) { ["_type"] = "filler", ["x"] = r.Int("x"), ["y"] = r.Int("y"), ["width"] = r.Int("w"), ["height"] = r.Int("h") }));
            map["fillers"] = fillers;
            map["stylesFg"] = new Table(s);
            map["stylesBg"] = new Table(s);
            map["package"] = _map.Package;
            map["_type"] = "map";
            return map;
        }

        private static Table Items(Script s, BinElement? list, string type)
        {
            var arr = new Table(s);
            foreach (var e in list?.Children ?? new())
            {
                var t = new Table(s) { ["_name"] = e.Name, ["_type"] = type };
                foreach (var a in e.Attrs)
                    if (a.Key == "id") t["_id"] = ToLua(a.Value);
                    else t[a.Key] = ToLua(a.Value);
                var nodes = MapCanvas.Nodes(e).ToList();
                if (nodes.Count > 0)
                {
                    var nt = new Table(s);
                    foreach (var n in nodes) nt.Append(DynValue.NewTable(new Table(s) { ["x"] = n.Num("x"), ["y"] = n.Num("y") }));
                    t["nodes"] = nt;
                }
                arr.Append(DynValue.NewTable(t));
            }
            return arr;
        }

        private static Table Decals(Script s, BinElement? list)
        {
            var arr = new Table(s);
            foreach (var d in list?.Children ?? new())
            {
                var t = new Table(s) { ["_type"] = "decal" };
                foreach (var a in d.Attrs) t[a.Key] = ToLua(a.Value);
                string tex = d.Str("texture");
                t["texture"] = "decals/" + (tex.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? tex[..^4] : tex);
                arr.Append(DynValue.NewTable(t));
            }
            return arr;
        }

        public void WriteBack(Script s, Table map, RunResult result)
        {
            var levelsEl = MapModel.Levels(_map);
            var claimed = new HashSet<BinElement>();
            var toString = s.Globals.Get("__tileString");
            int nextId = MapModel.NextId(_map);
            var rooms = map.Get("rooms").Type == DataType.Table ? map.Get("rooms").Table : new Table(s);
            for (int i = 1; i <= rooms.Length; i++)
            {
                if (rooms.Get(i).Type != DataType.Table) continue;
                var t = rooms.Get(i).Table;
                BinElement? l = null;
                if (t.Get("__mtz").Type == DataType.Number && (int)t.Get("__mtz").Number - 1 is var idx && idx >= 0 && idx < _levels.Count && !claimed.Contains(_levels[idx]))
                    l = _levels[idx];
                bool added = false;
                if (l == null)
                {
                    string name = t.Get("name").Type == DataType.String ? t.Get("name").String : "room";
                    name = MapModel.UniqueName(_map, name);
                    l = MapModel.NewLevel(name, 0, 0, 8, 8);
                    levelsEl.Children.Add(l);
                    added = true;
                    result.RoomsAdded++;
                }
                claimed.Add(l);
                if (WriteRoom(s, t, l, toString, ref nextId, added) && !added) result.RoomsChanged++;
            }
            foreach (var l in _levels.Where(l => !claimed.Contains(l)))
            {
                levelsEl.Children.Remove(l);
                result.RoomsRemoved++;
            }

            // fillers
            var oldF = (_map.Root.Child("Filler")?.Children ?? new()).Where(c => c.Name == "rect").Select(r => (r.Int("x"), r.Int("y"), r.Int("w"), r.Int("h"))).ToList();
            var newF = new List<(int, int, int, int)>();
            if (map.Get("fillers") is { Type: DataType.Table } ft)
                foreach (var f in ft.Table.Values.Where(v => v.Type == DataType.Table).Select(v => v.Table))
                    newF.Add(((int)f.Get("x").CastToNumber().GetValueOrDefault(), (int)f.Get("y").CastToNumber().GetValueOrDefault(),
                              (int)f.Get("width").CastToNumber().GetValueOrDefault(), (int)f.Get("height").CastToNumber().GetValueOrDefault()));
            if (!oldF.SequenceEqual(newF))
            {
                var fe = Fillers.Element(_map);
                fe.Children.RemoveAll(c => c.Name == "rect");
                foreach (var (x, y, w, h) in newF)
                {
                    var r = new BinElement("rect");
                    r.Set("x", x); r.Set("y", y); r.Set("w", w); r.Set("h", h);
                    fe.Children.Add(r);
                }
                result.FillersChanged = true;
            }
        }

        /// <summary>Applies a room table to its level. Returns true when anything changed.</summary>
        private static bool WriteRoom(Script s, Table t, BinElement l, DynValue toString, ref int nextId, bool fresh)
        {
            bool changed = false;
            void Set(string key, object? v)
            {
                if (v == null) return;
                var a = l.Find(key);
                if (a != null && Equals(Norm(a.Value), Norm(v))) return;
                l.Set(key, v);
                changed = true;
            }
            int oldCols = Math.Max(1, (l.Int("width") + 7) / 8), oldRows = Math.Max(1, (l.Int("height") + 7) / 8);
            foreach (string k in new[] { "name", "x", "y", "width", "height" })
                Set(k, FromLua(t.Get(k), l.Find(k)));
            if (t.Get("color").Type == DataType.Number) Set("c", (int)t.Get("color").Number);
            foreach (var p in t.Pairs)
                if (p.Key.Type == DataType.String && !RoomSpecial.Contains(p.Key.String) && !p.Key.String.StartsWith("__"))
                    Set(p.Key.String, FromLua(p.Value, l.Find(p.Key.String)));
            foreach (var a in l.Attrs.ToList())
                if (a.Key is not ("c" or "color") && !RoomSpecial.Contains(a.Key) && t.Get(a.Key).IsNil()) { l.Remove(a.Key); changed = true; }

            int cols = Math.Max(1, (l.Int("width") + 7) / 8), rows = Math.Max(1, (l.Int("height") + 7) / 8);
            foreach (var (key, el) in new[] { ("tilesFg", "solids"), ("tilesBg", "bg") })
            {
                if (t.Get(key) is not { Type: DataType.Table } tiles || tiles.Table.Get("matrix").Type != DataType.Table) continue;
                string text = s.Call(toString, tiles.Table.Get("matrix")).CastToString() ?? "";
                string now = TileGrid.Parse(text, cols, rows).Serialize();
                var child = l.Child(el);
                string before = child == null ? "" : TileGrid.Parse(child.Str("innerText"), oldCols, oldRows).Serialize();
                if (now == before && cols == oldCols && rows == oldRows && child != null) continue;
                if (child == null) { child = new BinElement(el); l.Children.Add(child); }
                if (child.Find("innerText") is { } ia) ia.Value = now;
                else child.Attrs.Add(new BinAttr("innerText", BinType.Rle, now));
                changed = true;
            }

            changed |= WriteList(t.Get("entities"), l, "entities", ref nextId, decal: false);
            changed |= WriteList(t.Get("triggers"), l, "triggers", ref nextId, decal: false);
            changed |= WriteList(t.Get("decalsFg"), l, "fgdecals", ref nextId, decal: true);
            changed |= WriteList(t.Get("decalsBg"), l, "bgdecals", ref nextId, decal: true);
            return changed || fresh;
        }

        private static bool WriteList(DynValue v, BinElement level, string listName, ref int nextId, bool decal)
        {
            if (v.Type != DataType.Table) return false;
            var list = level.Child(listName);
            if (list == null) { list = new BinElement(listName); level.Children.Add(list); }
            var byId = decal ? new() : list.Children.GroupBy(e => e.Int("id")).ToDictionary(g => g.Key, g => g.First());
            var usedIds = new HashSet<int>();
            var built = new List<BinElement>();
            int di = 0;
            foreach (var item in v.Table.Values.Where(x => x.Type == DataType.Table).Select(x => x.Table))
            {
                BinElement? orig;
                BinElement el;
                if (decal)
                {
                    orig = di < list.Children.Count ? list.Children[di] : null;
                    di++;
                    el = new BinElement("decal");
                    foreach (var p in item.Pairs)
                    {
                        if (p.Key.Type != DataType.String || p.Key.String.StartsWith('_')) continue;
                        object? val = FromLua(p.Value, orig?.Find(p.Key.String));
                        if (p.Key.String == "texture" && val is string tex)
                        {
                            tex = tex.StartsWith("decals/") ? tex[7..] : tex;
                            if (orig?.Str("texture") is { } ot && ot.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !tex.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) tex += ".png";
                            val = tex;
                        }
                        if (val != null) el.Set(p.Key.String, val);
                    }
                    if (orig != null) el.Attrs = Ordered(el.Attrs, orig);
                }
                else
                {
                    int id = item.Get("_id").Type == DataType.Number ? (int)item.Get("_id").Number : -1;
                    orig = byId.GetValueOrDefault(id);
                    if (id < 0 || !usedIds.Add(id)) { id = nextId++; usedIds.Add(id); }
                    string name = item.Get("_name").Type == DataType.String ? item.Get("_name").String : orig?.Name ?? "unknown";
                    el = new BinElement(name);
                    el.Set("id", id);
                    foreach (var p in item.Pairs)
                    {
                        if (p.Key.Type != DataType.String || p.Key.String.StartsWith('_') || p.Key.String == "nodes") continue;
                        if (FromLua(p.Value, orig?.Find(p.Key.String)) is { } val) el.Set(p.Key.String, val);
                    }
                    if (orig != null) el.Attrs = Ordered(el.Attrs, orig);
                    if (item.Get("nodes") is { Type: DataType.Table } nodes)
                        foreach (var n in nodes.Table.Values.Where(x => x.Type == DataType.Table).Select(x => x.Table))
                        {
                            var ne = new BinElement("node");
                            MapCanvas.SetNum(ne, "x", (float)n.Get("x").CastToNumber().GetValueOrDefault());
                            MapCanvas.SetNum(ne, "y", (float)n.Get("y").CastToNumber().GetValueOrDefault());
                            el.Children.Add(ne);
                        }
                    if (orig != null) el.Children.AddRange(orig.Children.Where(c => c.Name != "node").Select(c => c.Clone()));
                }
                built.Add(el);
            }
            if (built.Count == list.Children.Count && built.Zip(list.Children).All(p => Same(p.First, p.Second))) return false;
            list.Children.Clear();
            list.Children.AddRange(built);
            return true;
        }

        private static List<BinAttr> Ordered(List<BinAttr> attrs, BinElement orig) =>
            attrs.OrderBy(a => orig.Attrs.FindIndex(o => o.Key == a.Key) is var i && i >= 0 ? i : int.MaxValue).ToList();

        private static object? Norm(object? v) => v is IConvertible c && v is not (string or bool) ? c.ToDouble(CultureInfo.InvariantCulture) : v;

        private static bool Same(BinElement a, BinElement b) =>
            a.Name == b.Name && a.Attrs.Count == b.Attrs.Count && a.Children.Count == b.Children.Count
            && a.Attrs.All(x => b.Find(x.Key) is { } y && Equals(Norm(x.Value), Norm(y.Value)))
            && a.Children.Zip(b.Children).All(p => Same(p.First, p.Second));
    }

    // ---- the emulated Lönn API ----

    private const string Prelude = """
        local smt = {}
        local function stub() return setmetatable({}, smt) end
        smt.__index = function(t, k) if type(k) == "number" then return nil end local d = stub() rawset(t, k, d) return d end
        smt.__call = function() return stub() end
        local zero = function() return 0 end
        smt.__add = zero smt.__sub = zero smt.__mul = zero smt.__div = zero smt.__mod = zero smt.__unm = zero smt.__len = zero
        smt.__concat = function() return "" end

        local M = {}
        local function module(name, t)
          M[name] = setmetatable(t, { __index = function(_, k) return stub() end })
          return t
        end

        -- utils.matrix
        local Matrix = {}
        Matrix.__index = Matrix
        local function filled(def, w, h)
          local m = setmetatable({ _width = w, _height = h, _default = def, _data = {} }, Matrix)
          for i = 1, w * h do m._data[i] = def end
          return m
        end
        function Matrix:size() return self._width, self._height end
        function Matrix:inbounds(x, y) return x >= 1 and y >= 1 and x <= self._width and y <= self._height end
        function Matrix:inbounds0(x, y) return self:inbounds(x + 1, y + 1) end
        function Matrix:getInbounds(x, y) return self._data[(y - 1) * self._width + x] end
        function Matrix:get0Inbounds(x, y) return self._data[y * self._width + x + 1] end
        function Matrix:get(x, y, def) if self:inbounds(x, y) then return self._data[(y - 1) * self._width + x] end return def end
        function Matrix:get0(x, y, def) return self:get(x + 1, y + 1, def) end
        function Matrix:setInbounds(x, y, v) self._data[(y - 1) * self._width + x] = v end
        function Matrix:set(x, y, v) if self:inbounds(x, y) then self._data[(y - 1) * self._width + x] = v end end
        function Matrix:set0(x, y, v) self:set(x + 1, y + 1, v) end
        function Matrix:clone() local m = filled(self._default, self._width, self._height) for i, v in ipairs(self._data) do m._data[i] = v end return m end
        function Matrix:copy() return self:clone() end
        function Matrix:fill(v) for i = 1, self._width * self._height do self._data[i] = v end end
        function Matrix:resize(w, h, def)
          local m = filled(def or self._default, w, h)
          for y = 1, math.min(h, self._height) do for x = 1, math.min(w, self._width) do m:set(x, y, self:get(x, y)) end end
          self._width, self._height, self._data = w, h, m._data
          return self
        end
        local matrixLib = module("utils.matrix", { filled = filled })
        function matrixLib.fromTable(t, w, h) local m = filled("0", w, h) for i = 1, w * h do m._data[i] = t[i] end return m end
        M["matrix"] = matrixLib

        -- structs.tiles
        local tiles = module("structs.tiles", {})
        local function lines(s)
          local out = {}
          s = (s or ""):gsub("\r", "")
          for line in (s .. "\n"):gmatch("(.-)\n") do table.insert(out, line) end
          if #out > 0 and out[#out] == "" then table.remove(out) end
          return out
        end
        function tiles.tileStringToMatrix(s, w, h)
          local ls = lines(s)
          if not w then w = 0 for _, l in ipairs(ls) do if #l > w then w = #l end end end
          h = h or #ls
          local m = filled("0", w, h)
          for y = 1, math.min(h, #ls) do
            local l = ls[y]
            for x = 1, math.min(w, #l) do local c = l:sub(x, x) if c == " " then c = "0" end m:set(x, y, c) end
          end
          return m
        end
        function tiles.matrixToTileString(m)
          local rows = {}
          local w, h = m:size()
          for y = 1, h do
            local r = {}
            for x = 1, w do r[x] = tostring(m:get(x, y, "0")) end
            rows[y] = table.concat(r)
          end
          return table.concat(rows, "\n")
        end
        function tiles.matrixToTileStringMinimized(m)
          local rows = lines(tiles.matrixToTileString(m))
          for i, r in ipairs(rows) do rows[i] = r:gsub("0+$", "") end
          while #rows > 0 and rows[#rows] == "" do table.remove(rows) end
          return table.concat(rows, "\n")
        end
        function tiles.decode(data) return { _type = "tiles", matrix = tiles.tileStringToMatrix(data and data.innerText or "") } end
        function tiles.encode(t) return { innerText = tiles.matrixToTileString(t.matrix) } end
        __mkTiles = function(s, w, h) return { _type = "tiles", matrix = tiles.tileStringToMatrix(s, w, h) } end
        __tileString = function(m) return tiles.matrixToTileString(m) end
        module("structs.object_tiles", { decode = function() return { matrix = filled("-1", 0, 0) } end })

        -- utils
        local utils = module("utils", {})
        M["utils.utils"] = utils
        function utils.deepcopy(t, seen)
          if type(t) ~= "table" then return t end
          seen = seen or {}
          if seen[t] then return seen[t] end
          local c = {}
          seen[t] = c
          for k, v in pairs(t) do c[utils.deepcopy(k, seen)] = utils.deepcopy(v, seen) end
          return setmetatable(c, getmetatable(t))
        end
        function utils.shallowcopy(t) local c = {} for k, v in pairs(t) do c[k] = v end return setmetatable(c, getmetatable(t)) end
        function utils.callIfFunction(f, ...) if type(f) == "function" then return f(...) end return f end
        function utils.contains(v, t) for _, x in pairs(t) do if x == v then return true end end return false end
        function utils.round(n, d) local m = 10 ^ (d or 0) return math.floor(n * m + 0.5) / m end
        function utils.clamp(v, a, b) return math.max(a, math.min(b, v)) end
        function utils.mod1(a, b) return ((a - 1) % b) + 1 end
        function utils.isInteger(n) return type(n) == "number" and math.floor(n) == n end
        function utils.titleCase(s) return (s:gsub("(%a)([%w_']*)", function(a, b) return a:upper() .. b end)) end
        function utils.humanizeVariableName(s) return utils.titleCase((s:gsub("_", " "):gsub("(%l)(%u)", "%1 %2"))) end
        function utils.trim(s) return (s:gsub("^%s+", ""):gsub("%s+$", "")) end
        function utils.split(s, sep) local out = {} for p in string.gmatch(s, "([^" .. (sep or ",") .. "]+)") do table.insert(out, p) end return out end
        function utils.startsWith(s, p) return s:sub(1, #p) == p end
        function utils.endsWith(s, p) return p == "" or s:sub(-#p) == p end
        function utils.aabbCheckInline(x1, y1, w1, h1, x2, y2, w2, h2) return x1 < x2 + w2 and x2 < x1 + w1 and y1 < y2 + h2 and y2 < y1 + h1 end
        function utils.rectangle(x, y, w, h) return { x = x, y = y, width = w, height = h } end
        function utils.parseHexColor(s)
          if type(s) ~= "string" then return false, 0, 0, 0 end
          s = s:gsub("^#", "")
          if #s ~= 6 and #s ~= 8 then return false, 0, 0, 0 end
          local r, g, b = tonumber(s:sub(1, 2), 16), tonumber(s:sub(3, 4), 16), tonumber(s:sub(5, 6), 16)
          if not (r and g and b) then return false, 0, 0, 0 end
          local a = #s == 8 and tonumber(s:sub(7, 8), 16) or 255
          return true, r / 255, g / 255, b / 255, a / 255
        end
        local function hex(v) return string.format("%02x", math.floor(utils.clamp(v, 0, 1) * 255 + 0.5)) end
        function utils.rgbToHex(r, g, b) return hex(r) .. hex(g) .. hex(b) end
        function utils.rgbaToHex(r, g, b, a) return hex(r) .. hex(g) .. hex(b) .. hex(a or 1) end
        function utils.hsvToRgb(h, s, v)
          h = (h % 360) / 60
          local c = v * s
          local x = c * (1 - math.abs(h % 2 - 1))
          local m = v - c
          local r, g, b = 0, 0, 0
          if h < 1 then r, g, b = c, x, 0 elseif h < 2 then r, g, b = x, c, 0 elseif h < 3 then r, g, b = 0, c, x
          elseif h < 4 then r, g, b = 0, x, c elseif h < 5 then r, g, b = x, 0, c else r, g, b = c, 0, x end
          return r + m, g + m, b + m
        end
        function utils.rgbToHsv(r, g, b)
          local mx, mn = math.max(r, g, b), math.min(r, g, b)
          local d = mx - mn
          local h = 0
          if d > 0 then
            if mx == r then h = 60 * (((g - b) / d) % 6) elseif mx == g then h = 60 * ((b - r) / d + 2) else h = 60 * ((r - g) / d + 4) end
          end
          return h / 360, mx == 0 and 0 or d / mx, mx
        end
        function utils.serialize(v, indent)
          indent = indent or ""
          if type(v) ~= "table" then return type(v) == "string" and string.format("%q", v) or tostring(v) end
          local parts = {}
          for k, x in pairs(v) do table.insert(parts, indent .. "  [" .. utils.serialize(k) .. "] = " .. utils.serialize(x, indent .. "  ")) end
          return "{\n" .. table.concat(parts, ",\n") .. "\n" .. indent .. "}"
        end
        function utils.log(...) print(...) end

        module("logging", {
          info = function(...) print(...) end, warning = function(...) print("warning: ", ...) end,
          error = function(...) print("error: ", ...) end, debug = function() end, log = function(_, ...) print(...) end
        })

        -- loaded_state, map items, rooms, fillers
        local state = module("loaded_state", { selectedItem = nil, refreshMap = function() end, refresh = function() end })
        __state = state
        function state.getSelectedRoom() return rawget(state, "__current") end
        function state.getSelectedItem() return rawget(state, "__current") end
        function state.getRoomByName(name)
          for _, r in ipairs(state.map.rooms) do if r.name == name or r.name == "lvl_" .. name then return r end end
        end
        local mapItemUtils = module("map_item_utils", {})
        function mapItemUtils.addItem(map, item)
          if item._type == "filler" then table.insert(map.fillers, item) else table.insert(map.rooms, item) end
        end
        function mapItemUtils.deleteItem(map, item)
          for _, list in ipairs({ map.rooms, map.fillers }) do
            for i = #list, 1, -1 do if list[i] == item then table.remove(list, i) end end
          end
        end
        mapItemUtils.deleteRoom = mapItemUtils.deleteItem
        mapItemUtils.deleteFiller = mapItemUtils.deleteItem

        local roomStruct = module("structs.room", {})
        function roomStruct.decode(data)
          local room = { _type = "room", entities = {}, triggers = {}, decalsFg = {}, decalsBg = {},
            musicLayer1 = true, musicLayer2 = true, musicLayer3 = true, musicLayer4 = true, windPattern = "None", color = 0,
            cameraOffsetX = 0, cameraOffsetY = 0, dark = false, space = false, underwater = false, whisper = false,
            disableDownTransition = false, music = "", alt_music = "", ambience = "", musicProgress = "", ambienceProgress = "" }
          for k, v in pairs(data or {}) do if k ~= "__children" and k ~= "__name" then room[k] = v end end
          room.x, room.y = room.x or 0, room.y or 0
          room.width, room.height = room.width or 320, room.height or 184
          local w, h = math.floor(room.width / 8), math.floor(room.height / 8)
          local fg, bg = "", ""
          for _, c in ipairs((data or {}).__children or {}) do
            if c.__name == "solids" then fg = c.innerText or "" elseif c.__name == "bg" then bg = c.innerText or "" end
          end
          room.tilesFg = __mkTiles(fg, w, h)
          room.tilesBg = __mkTiles(bg, w, h)
          return room
        end
        local function shiftList(list, dx, dy)
          for _, e in ipairs(list or {}) do
            e.x, e.y = (e.x or 0) + dx, (e.y or 0) + dy
            for _, n in ipairs(e.nodes or {}) do n.x, n.y = n.x + dx, n.y + dy end
          end
        end
        function roomStruct.directionalResize(room, side, amount)
          local w, h = math.floor(room.width / 8), math.floor(room.height / 8)
          local ox, oy, nw, nh = 0, 0, w, h
          if side == "up" then oy = amount nh = h + amount room.y = room.y - amount * 8
          elseif side == "down" then nh = h + amount
          elseif side == "left" then ox = amount nw = w + amount room.x = room.x - amount * 8
          elseif side == "right" then nw = w + amount end
          nw, nh = math.max(0, nw), math.max(0, nh)
          for _, key in ipairs({ "tilesFg", "tilesBg" }) do
            local old = room[key] and room[key].matrix
            local m = filled("0", nw, nh)
            if old then for y = 1, nh do for x = 1, nw do m:set(x, y, old:get(x - ox, y - oy, "0")) end end end
            room[key] = { _type = "tiles", matrix = m }
          end
          room.width, room.height = nw * 8, nh * 8
          if ox ~= 0 or oy ~= 0 then
            for _, l in ipairs({ room.entities, room.triggers, room.decalsFg, room.decalsBg }) do shiftList(l, ox * 8, oy * 8) end
          end
        end
        function roomStruct.getPosition(room) return room.x, room.y end
        function roomStruct.getSize(room) return room.width, room.height end

        module("structs.filler", {
          getPosition = function(f) return f.x * 8, f.y * 8 end,
          getSize = function(f) return f.width * 8, f.height * 8 end,
          decode = function(d) return { _type = "filler", x = d.x, y = d.y, width = d.w or d.width, height = d.h or d.height } end
        })
        module("structs.snapshot", { create = function(name, data, backward, forward) return { name = name, data = data, backward = backward, forward = forward } end })
        module("structs.decal", { decode = function(d) local c = utils.deepcopy(d) c._type = "decal" return c end })
        module("structs.entity", { decode = function(d) local c = utils.deepcopy(d) c._type = "entity" return c end })
        module("structs.trigger", { decode = function(d) local c = utils.deepcopy(d) c._type = "trigger" return c end })

        -- registered entities/triggers come from MT_Zenith's plugin catalog
        local registered = setmetatable({}, { __index = function(t, k) local h = __entityHandler(k) if h then rawset(t, k, h) end return h end })
        module("entities", { registeredEntities = registered })
        module("triggers", { registeredTriggers = registered })
        module("mods", { requireFromPlugin = function() return stub() end, getModSettings = function() return {} end, getModPersistence = function() return {} end })
        module("history", { addSnapshot = function() end })
        module("tools", {})
        module("ui.notification", { notify = function(s) print(s) end })
        M["notification"] = M["ui.notification"]

        __pending = nil
        __scriptsTool = { useScript = function(handler) __pending = handler end }

        require = function(name)
          local m = M[name]
          if m then return m end
          return stub()
        end
        love = stub()
        """;
}
