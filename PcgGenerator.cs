using System.Diagnostics;
using System.Globalization;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Loaders;

namespace CELMapViewer;

public sealed class PcgOptions
{
    public string Seed = "";
    public int NbRooms = 7;
    public double Proba = 0.3;
    public int RoomW = 40, RoomH = 23;
    public int TriesLimit = 10;
    public int BtDepth;
    public bool ResetSkeleton;
    public string Model = "pe_d5_full";
    public string Background = "none";
    public string Solid = "1";
}

public sealed class PcgEntity
{
    public string Name = "";
    public int X, Y;
    public int? W, H;
    public List<(string Key, object Value)> Attrs = new();
}

public sealed class PcgRoom
{
    public string Name = "";
    public int X, Y, W, H, WTiles, HTiles;
    public string[] Fg = Array.Empty<string>();
    public string[]? Bg;
    public List<PcgEntity> Entities = new();
}

public sealed class PcgResult
{
    public string Seed = "";
    public List<PcgRoom> Rooms = new();
    public List<string> Route = new();
    public bool AllPlayable;
    public TimeSpan Elapsed;
}

/// <summary>
/// Runs PCGHelper's seleCte pure-Lua generator (Mods/PCGHelper/Loenn/library/selecte)
/// inside MoonSharp, with a LuaJIT-compatible <c>bit</c> library so a seed gives the same
/// level as PCGHelper's Lönn script.
/// </summary>
public sealed class PcgGenerator
{
    public static readonly string[] Models =
        { "pe_d5_full", "fg_1", "fg_2", "fg_3", "fg_4", "fg_5", "fg_6", "fg_7", "fg_8", "fg_9" };
    public static readonly string[] Backgrounds =
        { "none", "bg_1", "bg_2", "bg_3", "bg_4", "bg_5", "bg_6", "bg_7", "bg_8", "bg_9" };

    public string LoennRoot { get; }
    private volatile bool _cancel;

    public PcgGenerator(string loennRoot) { LoennRoot = loennRoot; }

    /// <summary>Finds Mods/PCGHelper/Loenn (or a PCGHelper zip extracted under any name containing "PCGHelper").</summary>
    public static string? FindLoennRoot(string? modDir)
    {
        var mods = modDir != null ? Directory.GetParent(modDir)?.FullName : null;
        if (mods == null) return null;
        foreach (string dir in Directory.EnumerateDirectories(mods).OrderBy(d => !d.EndsWith("PCGHelper", StringComparison.OrdinalIgnoreCase)))
        {
            if (!Path.GetFileName(dir).Contains("PCGHelper", StringComparison.OrdinalIgnoreCase)) continue;
            string root = Path.Combine(dir, "Loenn");
            if (File.Exists(Path.Combine(root, "library", "selecte", "level.lua"))) return root;
        }
        return null;
    }

    public void Cancel() => _cancel = true;

    private Script CreateScript(Action<string> log)
    {
        var script = new Script(CoreModules.Preset_SoftSandbox | CoreModules.OS_Time | CoreModules.LoadMethods);
        script.Options.ScriptLoader = new FileSystemScriptLoader
        {
            ModulePaths = new[] { Path.Combine(LoennRoot, "?.lua").Replace('\\', '/') }
        };
        script.Options.DebugPrint = s => log(s);

        script.Globals["__bit"] = BitLibrary(script);
        script.Globals["__log"] = DynValue.NewCallback((_, args) =>
        {
            if (_cancel) throw new ScriptRuntimeException("cancelled");
            log(args.Count > 0 ? args[0].ToPrintString() : "");
            return DynValue.Nil;
        });
        script.DoString("package.loaded['bit'] = __bit");
        return script;
    }

    /// <summary>Lists the solid tileset glyphs the alphabet knows, with names.</summary>
    public List<(string Glyph, string Name)> SolidTilesets()
    {
        var script = CreateScript(_ => { });
        var t = script.DoString("return require('library.selecte.alphabet').FG_TILE_NAMES").Table;
        var list = new List<(string, string)>();
        foreach (var pair in t.Pairs)
            list.Add((pair.Key.ToPrintString(), pair.Value.ToPrintString()));
        list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    public PcgResult Generate(PcgOptions o, Action<string> log)
    {
        _cancel = false;
        var sw = Stopwatch.StartNew();
        var script = CreateScript(log);

        var opts = new Table(script);
        foreach (var kv in new Dictionary<string, object>
        {
            ["seed"] = o.Seed,
            ["nbRooms"] = o.NbRooms,
            ["proba"] = o.Proba,
            ["roomW"] = o.RoomW,
            ["roomH"] = o.RoomH,
            ["triesLimit"] = o.TriesLimit,
            ["btDepth"] = o.BtDepth,
            ["resetSkeleton"] = o.ResetSkeleton,
            ["model"] = o.Model,
            ["background"] = o.Background == "none" ? "" : o.Background,
            ["solid"] = o.Solid,
        })
            opts[kv.Key] = kv.Value;
        script.Globals["__opts"] = opts;

        const string flow = @"
local o = __opts
local Rng = require('library.selecte.rng').Rng
local dpeData = require('library.selecte.dpe_data')
local level = require('library.selecte.level')
local encoder = require('library.selecte.encoder')

local rng = Rng.new(o.seed)
local dpe = dpeData[o.model]
if not dpe then error('unknown model ' .. tostring(o.model)) end
local bgDpe = nil
if o.background ~= '' then
  bgDpe = dpeData[o.background]
  if not bgDpe then error('unknown background model ' .. tostring(o.background)) end
end

__log(string.format('seleCte: seed %q, %d rooms of %dx%d, proba %.2f, model %s, background %s',
  tostring(rng.seed), o.nbRooms, o.roomW, o.roomH, o.proba, o.model, o.background ~= '' and o.background or 'none'))

local skeleton, route, summary = level.generate(dpe, {
  nbRooms = o.nbRooms, proba = o.proba, roomSize = { o.roomW, o.roomH },
  btDepth = o.btDepth, triesLimit = o.triesLimit, resetSkeleton = o.resetSkeleton,
}, rng, __log)

local bgByName = nil
if bgDpe then
  bgByName = {}
  for _, name in ipairs(skeleton:listAllRooms()) do
    bgByName[name] = level.generateBackground(bgDpe, o.roomW, o.roomH, rng, o.btDepth)
  end
end

local rooms = encoder.encodeSkeleton(skeleton, { solid = o.solid }, bgByName)
return { seed = tostring(rng.seed), rooms = rooms, route = route, allPlayable = summary.allPlayable }
";
        Table res = script.DoString(flow).Table;
        var result = new PcgResult
        {
            Seed = res.Get("seed").ToPrintString(),
            AllPlayable = res.Get("allPlayable").CastToBool(),
            Elapsed = sw.Elapsed
        };
        foreach (var v in Seq(res.Get("route"))) result.Route.Add(v.ToPrintString());
        foreach (var rv in Seq(res.Get("rooms")))
        {
            Table r = rv.Table;
            var room = new PcgRoom
            {
                Name = r.Get("name").ToPrintString(),
                X = I(r, "x"), Y = I(r, "y"), W = I(r, "width"), H = I(r, "height"),
                WTiles = I(r, "widthTiles"), HTiles = I(r, "heightTiles"),
                Fg = Seq(r.Get("fgRows")).Select(x => x.String).ToArray(),
                Bg = r.Get("bgRows").IsNil() ? null : Seq(r.Get("bgRows")).Select(x => x.String).ToArray(),
            };
            foreach (var ev in Seq(r.Get("entities")))
            {
                Table e = ev.Table;
                var ent = new PcgEntity { Name = e.Get("name").String, X = I(e, "x"), Y = I(e, "y") };
                if (!e.Get("width").IsNil()) ent.W = I(e, "width");
                if (!e.Get("height").IsNil()) ent.H = I(e, "height");
                foreach (var a in e.Get("attrs").Table.Pairs.OrderBy(p => p.Key.ToPrintString(), StringComparer.Ordinal))
                    ent.Attrs.Add((a.Key.ToPrintString(), ToClr(a.Value)));
                room.Entities.Add(ent);
            }
            result.Rooms.Add(room);
        }
        result.Elapsed = sw.Elapsed;
        return result;
    }

    private static IEnumerable<DynValue> Seq(DynValue v)
    {
        if (v.Type != DataType.Table) yield break;
        for (int i = 1; i <= v.Table.Length; i++) yield return v.Table.Get(i);
    }

    private static int I(Table t, string key) => (int)Math.Round(t.Get(key).CastToNumber() ?? 0);

    private static object ToClr(DynValue v) => v.Type switch
    {
        DataType.Boolean => v.Boolean,
        DataType.Number when v.Number == Math.Floor(v.Number) && Math.Abs(v.Number) < int.MaxValue => (int)v.Number,
        DataType.Number => (float)v.Number,
        _ => v.ToPrintString()
    };

    // ---- LuaJIT `bit` library: 32-bit signed results, arguments normalised modulo 2^32 ----

    private static int ToBit(double x) => unchecked((int)(long)Math.Round(x, MidpointRounding.ToEven));

    private static Table BitLibrary(Script script)
    {
        var t = new Table(script);
        DynValue N(int v) => DynValue.NewNumber(v);
        int Arg(CallbackArguments a, int i) => ToBit(a.AsType(i, "bit", DataType.Number).Number);

        t["tobit"] = DynValue.NewCallback((_, a) => N(Arg(a, 0)));
        t["bnot"] = DynValue.NewCallback((_, a) => N(~Arg(a, 0)));
        t["band"] = DynValue.NewCallback((_, a) => { int r = Arg(a, 0); for (int i = 1; i < a.Count; i++) r &= Arg(a, i); return N(r); });
        t["bor"] = DynValue.NewCallback((_, a) => { int r = Arg(a, 0); for (int i = 1; i < a.Count; i++) r |= Arg(a, i); return N(r); });
        t["bxor"] = DynValue.NewCallback((_, a) => { int r = Arg(a, 0); for (int i = 1; i < a.Count; i++) r ^= Arg(a, i); return N(r); });
        t["lshift"] = DynValue.NewCallback((_, a) => N(unchecked((int)((uint)Arg(a, 0) << (Arg(a, 1) & 31)))));
        t["rshift"] = DynValue.NewCallback((_, a) => N(unchecked((int)((uint)Arg(a, 0) >> (Arg(a, 1) & 31)))));
        t["arshift"] = DynValue.NewCallback((_, a) => N(Arg(a, 0) >> (Arg(a, 1) & 31)));
        t["rol"] = DynValue.NewCallback((_, a) => N(unchecked((int)uint.RotateLeft((uint)Arg(a, 0), Arg(a, 1) & 31))));
        t["ror"] = DynValue.NewCallback((_, a) => N(unchecked((int)uint.RotateRight((uint)Arg(a, 0), Arg(a, 1) & 31))));
        t["bswap"] = DynValue.NewCallback((_, a) => N(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(Arg(a, 0))));
        t["tohex"] = DynValue.NewCallback((_, a) =>
        {
            int n = a.Count > 1 ? Arg(a, 1) : 8;
            string hex = ((uint)Arg(a, 0)).ToString(n < 0 ? "X8" : "x8", CultureInfo.InvariantCulture);
            return DynValue.NewString(hex[^Math.Clamp(Math.Abs(n), 1, 8)..]);
        });
        return t;
    }
}
