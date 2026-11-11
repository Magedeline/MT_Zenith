using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace CELMapViewer;

/// <summary>An entity or trigger type, read from a Lönn plugin (or the built-in vanilla list).</summary>
public sealed class PluginDef
{
    public required string Name;
    public bool Trigger;
    public string Source = "";
    public string? Texture;
    public PointF? Justify;
    public Color? Fill, Border;
    public int MinNodes;
    public int MaxNodes;                 // -1 = unlimited (Lönn's default nodeLimits is {0, 0})
    public Dictionary<string, FieldInfo> Fields = new(StringComparer.Ordinal);
    public List<string> FieldOrder = new();
    public List<PlacementDef> Placements = new();
}

/// <summary>Lönn <c>fieldInformation</c> for one attribute: its editor type and allowed options.</summary>
public sealed class FieldInfo
{
    public string? FieldType;            // integer, number, boolean, color, string, …
    public List<KeyValuePair<string, object>> Options = new();  // label → value
    public bool Editable = true;         // false = only the options are allowed
    public double? Min, Max;
}

public sealed class PlacementDef
{
    public required PluginDef Plugin;
    public required string Name;
    public List<KeyValuePair<string, object>> Data = new();

    public string Display => Plugin.Name + (Name.Length > 0 && !string.Equals(Name, Plugin.Name, StringComparison.OrdinalIgnoreCase) ? "  ·  " + Name : "");
    public override string ToString() => $"{Display}   [{Plugin.Source}]";

    /// <summary>A new map element for this placement (id/x/y set by the caller).</summary>
    public BinElement Create()
    {
        var el = new BinElement(Plugin.Name);
        foreach (var (k, v) in Data) el.Set(k, v);
        if (Plugin.Trigger)
        {
            if (el.Find("width") == null) el.Set("width", 16);
            if (el.Find("height") == null) el.Set("height", 16);
        }
        return el;
    }
}

/// <summary>
/// Entity/trigger definitions from every installed mod's Lönn plugins (this mod first), run in a sandboxed
/// MoonSharp with a permissive stand-in for <c>require</c>. Loads on a background thread; <see cref="Changed"/> fires as it goes.
/// </summary>
public static class PluginCatalog
{
    private static readonly ConcurrentDictionary<string, PluginDef> EntityDefs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, PluginDef> TriggerDefs = new(StringComparer.Ordinal);
    private static int _started;

    public static event Action? Changed;
    public static bool Loading { get; private set; }
    public static int FileCount, FailedCount;

    public static PluginDef? Entity(string name) => EntityDefs.GetValueOrDefault(name);
    public static PluginDef? Trigger(string name) => TriggerDefs.GetValueOrDefault(name);

    public static List<PlacementDef> AllPlacements() =>
        EntityDefs.Values.Concat(TriggerDefs.Values).SelectMany(p => p.Placements)
                  .OrderBy(p => p.Plugin.Source != "CEL").ThenBy(p => p.Display, StringComparer.OrdinalIgnoreCase).ToList();

    public static void StartLoading()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        foreach (var d in VanillaDefs()) (d.Trigger ? TriggerDefs : EntityDefs)[d.Name] = d;
        Loading = true;
        var t = new Thread(() =>
        {
            try { LoadAll(); }
            catch { /* best effort */ }
            finally { Loading = false; Changed?.Invoke(); }
        }) { IsBackground = true, Name = "PluginCatalog", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    /// <summary>Makes sure installed mods' plugins are loaded (loads them now, or waits for the background load).</summary>
    public static void EnsureLoaded()
    {
        if (Volatile.Read(ref _started) == 0) { LoadNow(); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Loading && sw.Elapsed < TimeSpan.FromMinutes(2)) Thread.Sleep(50);
    }

    /// <summary>Synchronous load (self-test).</summary>
    public static void LoadNow()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            foreach (var d in VanillaDefs()) (d.Trigger ? TriggerDefs : EntityDefs)[d.Name] = d;
        LoadAll();
    }

    private static void LoadAll()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (bool trigger in new[] { false, true })
        {
            var files = GameAssets.EnumerateModFiles(trigger ? "Loenn/triggers" : "Loenn/entities", ".lua").ToList();
            foreach (var (src, rel, read) in files)
            {
                byte[]? bytes = read();
                if (bytes == null) continue;
                FileCount++;
                foreach (var d in ParseFile(System.Text.Encoding.UTF8.GetString(bytes), src, rel, trigger))
                    (trigger ? TriggerDefs : EntityDefs).TryAdd(d.Name, d);
                if (sw.ElapsedMilliseconds > 400) { sw.Restart(); Changed?.Invoke(); }
            }
        }
    }

    // ---- Lua ----

    private const string Prelude = """
        local dmt = {}
        local function mk() return setmetatable({}, dmt) end
        dmt.__index = function(t, k) if type(k) == "number" then return nil end local d = mk() rawset(t, k, d) return d end
        dmt.__call = function() return mk() end
        local zero = function() return 0 end
        dmt.__add = zero dmt.__sub = zero dmt.__mul = zero dmt.__div = zero dmt.__mod = zero dmt.__pow = zero dmt.__unm = zero
        dmt.__concat = function() return "" end
        dmt.__len = zero
        dmt.__lt = function() return false end
        dmt.__le = function() return false end
        require = function() return mk() end
        mods = mk()
        love = mk()
        """;

    private static readonly Regex NameRx = new(@"\.name\s*=\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex TextureRx = new(@"\.texture\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    public static List<PluginDef> ParseFile(string code, string source, string rel, bool trigger)
    {
        var defs = new List<PluginDef>();
        try
        {
            var script = new Script(CoreModules.Preset_SoftSandbox);
            script.DoString(Prelude);
            var fn = script.LoadString(code, null, rel);
            var co = script.CreateCoroutine(fn).Coroutine;
            co.AutoYieldCounter = 200_000;
            var res = co.Resume();
            if (co.State != CoroutineState.Dead) return Fallback(code, source, trigger);
            if (res.Type == DataType.Tuple) res = res.Tuple.Length > 0 ? res.Tuple[0] : DynValue.Nil;
            if (res.Type != DataType.Table) return Fallback(code, source, trigger);
            var t = res.Table;
            if (t.Get("name").Type == DataType.String) Add(t);
            else
                for (int i = 1; i <= t.Length; i++)
                    if (t.Get(i).Type == DataType.Table) Add(t.Get(i).Table);
        }
        catch { return Fallback(code, source, trigger); }
        return defs.Count > 0 ? defs : Fallback(code, source, trigger);

        void Add(Table t)
        {
            if (t.Get("name").Type != DataType.String) return;
            var d = new PluginDef { Name = t.Get("name").String, Trigger = trigger, Source = source };
            if (t.Get("texture").Type == DataType.String) d.Texture = t.Get("texture").String;
            d.Justify = ReadVec(t.Get("justification"));
            d.Fill = ReadColor(t.Get("fillColor"));
            d.Border = ReadColor(t.Get("borderColor"));
            var nl = t.Get("nodeLimits");
            if (nl.Type == DataType.Table && nl.Table.Get(1).Type == DataType.Number) d.MinNodes = (int)nl.Table.Get(1).Number;
            if (nl.Type == DataType.Table && nl.Table.Get(2).Type == DataType.Number) d.MaxNodes = (int)nl.Table.Get(2).Number;
            else if (nl.Type == DataType.Table) d.MaxNodes = d.MinNodes;
            ReadFields(d, Resolve(t.Get("fieldInformation")));
            var fo = Resolve(t.Get("fieldOrder"));
            if (fo.Type == DataType.Table)
                for (int i = 1; i <= fo.Table.Length; i++)
                    if (fo.Table.Get(i).Type == DataType.String) d.FieldOrder.Add(fo.Table.Get(i).String);
            var pl = t.Get("placements");
            if (pl.Type == DataType.Table)
            {
                var pt = pl.Table;
                if (pt.Get("name").Type == DataType.String || pt.Get("data").Type == DataType.Table) AddPlacement(d, pt);
                else
                    for (int i = 1; i <= pt.Length; i++)
                        if (pt.Get(i).Type == DataType.Table) AddPlacement(d, pt.Get(i).Table);
            }
            if (d.Placements.Count == 0) d.Placements.Add(new PlacementDef { Plugin = d, Name = "" });
            defs.Add(d);
        }
    }

    /// <summary>A plugin value that may be a function of the entity (called with dummy arguments).</summary>
    private static DynValue Resolve(DynValue v)
    {
        if (v.Type != DataType.Function) return v;
        try
        {
            var script = v.Function.OwnerScript;
            var dummy = script.DoString("return require()");
            var co = script.CreateCoroutine(v).Coroutine;
            co.AutoYieldCounter = 100_000;
            var r = co.Resume(dummy, dummy);
            if (co.State != CoroutineState.Dead) return DynValue.Nil;
            return r.Type == DataType.Tuple ? (r.Tuple.Length > 0 ? r.Tuple[0] : DynValue.Nil) : r;
        }
        catch { return DynValue.Nil; }
    }

    private static object? Value(DynValue v) => v.Type switch
    {
        DataType.Boolean => v.Boolean,
        DataType.String => v.String,
        DataType.Number => v.Number == Math.Floor(v.Number) && Math.Abs(v.Number) < int.MaxValue ? (object)(int)v.Number : (float)v.Number,
        _ => null
    };

    private static void ReadFields(PluginDef d, DynValue info)
    {
        if (info.Type != DataType.Table) return;
        foreach (var kv in info.Table.Pairs)
        {
            if (kv.Key.Type != DataType.String || kv.Value.Type != DataType.Table) continue;
            var ft = kv.Value.Table;
            var f = new FieldInfo();
            if (ft.Get("fieldType").Type == DataType.String) f.FieldType = ft.Get("fieldType").String;
            if (ft.Get("editable").Type == DataType.Boolean) f.Editable = ft.Get("editable").Boolean;
            if (ft.Get("minimumValue").Type == DataType.Number) f.Min = ft.Get("minimumValue").Number;
            if (ft.Get("maximumValue").Type == DataType.Number) f.Max = ft.Get("maximumValue").Number;
            var opts = Resolve(ft.Get("options"));
            if (opts.Type == DataType.Table)
            {
                var ot = opts.Table;
                if (ot.Length > 0)
                    for (int i = 1; i <= ot.Length; i++)
                    {
                        var o = ot.Get(i);
                        if (o.Type == DataType.Table && Value(o.Table.Get(2)) is { } pv)
                            f.Options.Add(new(o.Table.Get(1).CastToString() ?? pv.ToString()!, pv));
                        else if (Value(o) is { } v) f.Options.Add(new(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!, v));
                    }
                else
                    foreach (var o in ot.Pairs)
                        if (o.Key.Type == DataType.String && Value(o.Value) is { } v) f.Options.Add(new(o.Key.String, v));
                if (ot.Length == 0) f.Options.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            }
            if (f.FieldType != null || f.Options.Count > 0) d.Fields[kv.Key.String] = f;
        }
    }

    private static void AddPlacement(PluginDef d, Table t)
    {
        var p = new PlacementDef { Plugin = d, Name = t.Get("name").Type == DataType.String ? t.Get("name").String : "" };
        if (t.Get("data").Type == DataType.Table)
            foreach (var kv in t.Get("data").Table.Pairs)
            {
                if (kv.Key.Type != DataType.String) continue;
                if (Value(kv.Value) is { } v) p.Data.Add(new(kv.Key.String, v));
            }
        p.Data.Sort((a, b) => Order(a.Key).CompareTo(Order(b.Key)) is var c && c != 0 ? c : string.CompareOrdinal(a.Key, b.Key));
        d.Placements.Add(p);

        static int Order(string k) => k switch { "width" => 0, "height" => 1, _ => 2 };
    }

    private static PointF? ReadVec(DynValue v) =>
        v.Type == DataType.Table && v.Table.Get(1).Type == DataType.Number && v.Table.Get(2).Type == DataType.Number
            ? new PointF((float)v.Table.Get(1).Number, (float)v.Table.Get(2).Number) : null;

    private static Color? ReadColor(DynValue v)
    {
        if (v.Type == DataType.String) return ParseHex(v.String);
        if (v.Type != DataType.Table || v.Table.Get(1).Type != DataType.Number) return null;
        double C(int i, double def) => v.Table.Get(i).Type == DataType.Number ? v.Table.Get(i).Number : def;
        int B(double x) => (int)Math.Clamp(x * 255, 0, 255);
        return Color.FromArgb(B(C(4, 1)), B(C(1, 0)), B(C(2, 0)), B(C(3, 0)));
    }

    public static Color? ParseHex(string s)
    {
        s = s.Trim().TrimStart('#');
        if (s.Length is not (6 or 8) || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return null;
        return s.Length == 6 ? Color.FromArgb(255, (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255)
                             : Color.FromArgb((int)v & 255, (int)(v >> 24) & 255, (int)(v >> 16) & 255, (int)(v >> 8) & 255);
    }

    private static List<PluginDef> Fallback(string code, string source, bool trigger)
    {
        Interlocked.Increment(ref FailedCount);
        var defs = new List<PluginDef>();
        string? tex = TextureRx.Match(code) is { Success: true } tm ? tm.Groups[1].Value : null;
        foreach (Match m in NameRx.Matches(code))
        {
            string name = m.Groups[1].Value;
            if (defs.Any(d => d.Name == name) || !(name.Contains('/') || char.IsLower(name[0]))) continue;
            var d = new PluginDef { Name = name, Trigger = trigger, Source = source, Texture = tex, MaxNodes = -1 };
            d.Placements.Add(new PlacementDef { Plugin = d, Name = "" });
            defs.Add(d);
        }
        // Placement names also match the regex; only keep the first (the plugin's own name) when unsure.
        return defs.Take(1).ToList();
    }

    // ---- vanilla ----

    /// <summary>Celeste's WindController.Patterns (room windPattern / wind trigger).</summary>
    public static readonly string[] WindPatterns =
    {
        "None", "Left", "Right", "LeftStrong", "RightStrong", "LeftOnOff", "RightOnOff", "LeftOnOffFast", "RightOnOffFast",
        "Alternating", "LeftGemsOnly", "RightCrazy", "Down", "Up", "Space"
    };
    private static readonly string[] Facings = { "Left", "Right" };
    private static readonly string[] PositionModes = { "NoEffect", "HorizontalCenter", "VerticalCenter", "TopToBottom", "BottomToTop", "LeftToRight", "RightToLeft" };
    private static readonly (string Def, string Key, string[] Options)[] VanillaOptions =
    {
        ("crystalStaticSpinner", "color", new[] { "Blue", "Red", "Purple", "Rainbow" }),
        ("spikesUp", "type", new[] { "default", "outline", "cliffside", "reflection", "tentacles" }),
        ("spikesDown", "type", new[] { "default", "outline", "cliffside", "reflection", "tentacles" }),
        ("spikesLeft", "type", new[] { "default", "outline", "cliffside", "reflection", "tentacles" }),
        ("spikesRight", "type", new[] { "default", "outline", "cliffside", "reflection", "tentacles" }),
        ("jumpThru", "texture", new[] { "wood", "dream", "temple", "templeB", "cliffside", "reflection", "core", "moon" }),
        ("spawnFacingTrigger", "facing", Facings),
        ("cameraTargetTrigger", "positionMode", PositionModes),
        ("bloomFadeTrigger", "positionMode", PositionModes),
        ("lightFadeTrigger", "positionMode", PositionModes),
        ("ambienceParamTrigger", "direction", PositionModes),
        ("musicFadeTrigger", "direction", new[] { "leftToRight", "topToBottom" }),
        ("windTrigger", "pattern", WindPatterns),
    };

    private static List<PluginDef> VanillaDefs()
    {
        var defs = VanillaDefsCore().ToList();
        foreach (var (name, key, opts) in VanillaOptions)
            if (defs.Find(d => d.Name == name) is { } d)
                d.Fields[key] = new FieldInfo { Editable = false, Options = opts.Select(o => new KeyValuePair<string, object>(o, o)).ToList() };
        return defs;
    }

    private static IEnumerable<PluginDef> VanillaDefsCore()
    {
        PluginDef E(string name, string? tex, float jx = 0.5f, float jy = 0.5f, params (string, object)[] data)
        {
            var d = new PluginDef { Name = name, Source = "Celeste", Texture = tex, Justify = new PointF(jx, jy) };
            var p = new PlacementDef { Plugin = d, Name = "" };
            foreach (var (k, v) in data) p.Data.Add(new(k, v));
            d.Placements.Add(p);
            return d;
        }
        PluginDef T(string name, params (string, object)[] data)
        {
            var d = new PluginDef { Name = name, Trigger = true, Source = "Celeste" };
            var p = new PlacementDef { Plugin = d, Name = "" };
            foreach (var (k, v) in data) p.Data.Add(new(k, v));
            d.Placements.Add(p);
            return d;
        }
        PluginDef Block(string name, string key, params (string, object)[] data)
        {
            var d = E(name, null, 0, 0, data);
            d.Placements[0].Data.InsertRange(0, new KeyValuePair<string, object>[] { new("width", 16), new("height", 16), new(key, "3") });
            return d;
        }

        yield return E("player", "characters/player/sitDown00", 0.5f, 1);
        yield return E("refill", "objects/refill/idle00", data: new (string, object)[] { ("twoDash", false), ("oneUse", false) });
        yield return E("spring", "objects/spring/00", 0.5f, 1, ("playerCanUse", true));
        yield return E("wallSpringLeft", "objects/spring/00", 0.5f, 1, ("playerCanUse", true));
        yield return E("wallSpringRight", "objects/spring/00", 0.5f, 1, ("playerCanUse", true));
        yield return E("strawberry", "collectables/strawberry/normal00", data: new (string, object)[] { ("winged", false), ("checkpointID", -1), ("order", -1) });
        yield return E("goldenBerry", "collectables/goldberry/idle00");
        yield return E("booster", "objects/booster/booster00", data: ("red", false));
        yield return E("cloud", "objects/clouds/cloud00", data: ("fragile", false));
        yield return E("bumper", "objects/Bumper/Idle22");
        yield return E("key", "collectables/key/idle00");
        yield return E("blackGem", "collectables/heartGem/0/00", data: ("fake", false));
        yield return E("cassette", "collectables/cassette/idle00");
        yield return E("infiniteStar", "objects/flyFeather/idle00", data: new (string, object)[] { ("shielded", false), ("singleUse", false) });
        yield return E("touchSwitch", "objects/touchswitch/container");
        yield return E("badelineBoost", "objects/badelineboost/idle00");
        yield return E("crystalStaticSpinner", "danger/crystal/fg_blue00", data: new (string, object)[] { ("color", "Blue"), ("attachToSolid", false) });
        yield return E("checkpoint", "objects/checkpoint/flag16");
        yield return E("lightning", null, 0, 0, ("width", 16), ("height", 16));
        yield return E("dreamBlock", null, 0, 0, ("width", 16), ("height", 16));
        yield return E("water", null, 0, 0, ("width", 16), ("height", 16));
        yield return E("jumpThru", null, 0, 0, ("width", 16), ("texture", "wood"));
        foreach (string dir in new[] { "Up", "Down", "Left", "Right" })
            yield return E("spikes" + dir, "danger/spikes/default_" + dir.ToLowerInvariant() + "00", 0, 0,
                (dir is "Up" or "Down" ? "width" : "height", (object)8), ("type", "default"));
        yield return Block("dashBlock", "tiletype", ("blendin", true), ("canDash", true), ("permanent", true));
        yield return Block("fakeWall", "tiletype");
        yield return Block("exitBlock", "tileType");
        yield return Block("fallingBlock", "tiles", ("climbFall", true), ("behind", false));
        yield return Block("floatySpaceBlock", "tiletype", ("disableSpawnOffset", false));
        yield return Block("crumbleWallOnRumble", "tiletype", ("blendin", true), ("persistent", false));

        yield return T("eventTrigger", ("event", ""));
        yield return T("cameraOffsetTrigger", ("cameraX", 0f), ("cameraY", 0f));
        yield return T("cameraTargetTrigger", ("lerpStrength", 0f), ("positionMode", "NoEffect"), ("xOnly", false), ("yOnly", false));
        yield return T("changeRespawnTrigger");
        yield return T("musicTrigger", ("track", ""), ("resetOnLeave", true), ("progress", 0));
        yield return T("musicFadeTrigger", ("direction", "leftToRight"), ("fadeA", 0f), ("fadeB", 1f), ("parameter", ""));
        yield return T("ambienceParamTrigger", ("parameter", ""), ("from", 0f), ("to", 0f), ("direction", "NoEffect"));
        yield return T("windTrigger", ("pattern", "None"));
        yield return T("noRefillTrigger", ("state", true));
        yield return T("spawnFacingTrigger", ("facing", "Right"));
        yield return T("bloomFadeTrigger", ("bloomAddFrom", 0f), ("bloomAddTo", 0f), ("positionMode", "NoEffect"));
        yield return T("lightFadeTrigger", ("lightAddFrom", 0f), ("lightAddTo", 0f), ("positionMode", "NoEffect"));
    }
}
