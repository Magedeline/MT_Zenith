using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public PcgOptions Clone() => (PcgOptions)MemberwiseClone();
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

/// <summary>A trained (custom) MdMC model saved under %APPDATA%\MT_Zenith\pcg_models.</summary>
public sealed class PcgModelInfo
{
    public string Name = "";
    public string Layer = "fg";
    public bool Builtin;
    public string? Path;
    public int Patterns, Rooms, Entities;
    public string? Sources;
    public DateTime? Created;
}

/// <summary>A room as the seleCte trainer wants it: fg/bg rows plus the vanilla-named entities it has symbols for.</summary>
public sealed class CorpusRoom
{
    public string Name = "";
    public string[] FgRows = Array.Empty<string>();
    public string[]? BgRows;
    public List<(string Name, float X, float Y, float? W, float? H, bool? Red)> Entities = new();
}

public sealed class TrainResult
{
    public JsonObject Dpe = new();
    public int Patterns, RoomsKept, RoomsTotal, Entities;
    public List<string> Skipped = new();
}

/// <summary>
/// Runs PCGHelper's seleCte pure-Lua generator (Mods/PCGHelper/Loenn/library/selecte) inside MoonSharp, with a
/// LuaJIT-compatible <c>bit</c> library so a seed gives the same level as PCGHelper's Lönn script. Besides the shipped
/// models it loads custom models trained here (<see cref="Train"/>) from any set of rooms, e.g. a collab tier.
/// </summary>
public sealed class PcgGenerator
{
    public static readonly string[] Models =
        { "pe_d5_full", "fg_1", "fg_2", "fg_3", "fg_4", "fg_5", "fg_6", "fg_7", "fg_8", "fg_9" };
    public static readonly string[] Backgrounds =
        { "none", "bg_1", "bg_2", "bg_3", "bg_4", "bg_5", "bg_6", "bg_7", "bg_8", "bg_9" };

    public static string CustomModelsDir => Path.Combine(AppInfo.DataDir, "pcg_models");

    public string LoennRoot { get; }
    private volatile bool _cancel;

    public PcgGenerator(string loennRoot) { LoennRoot = loennRoot; }

    /// <summary>Finds Mods/PCGHelper/Loenn (or a PCGHelper zip extracted under any name containing "PCGHelper"), or a remembered folder.</summary>
    public static string? FindLoennRoot(string? modDir)
    {
        static bool Ok(string? root) => root != null && File.Exists(Path.Combine(root, "library", "selecte", "level.lua"));
        foreach (string? env in new[] { Environment.GetEnvironmentVariable("MT_ZENITH_PCGHELPER"), AppSettings.Load().PcgHelperDir })
            if (env != null)
                foreach (string c in new[] { env, Path.Combine(env, "Loenn") })
                    if (Ok(c)) return c;
        var mods = modDir != null ? Directory.GetParent(modDir)?.FullName : GameAssets.ModsDir;
        if (mods == null || !Directory.Exists(mods)) return null;
        foreach (string dir in Directory.EnumerateDirectories(mods).OrderBy(d => !d.EndsWith("PCGHelper", StringComparison.OrdinalIgnoreCase)))
        {
            if (!Path.GetFileName(dir).Contains("PCGHelper", StringComparison.OrdinalIgnoreCase)) continue;
            string root = Path.Combine(dir, "Loenn");
            if (Ok(root)) return root;
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

    // ---- custom models ----

    public static List<PcgModelInfo> CustomModels()
    {
        var list = new List<PcgModelInfo>();
        if (!Directory.Exists(CustomModelsDir)) return list;
        foreach (string f in Directory.EnumerateFiles(CustomModelsDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                var meta = r.TryGetProperty("meta", out var m) ? m : default;
                list.Add(new PcgModelInfo
                {
                    Name = Path.GetFileNameWithoutExtension(f), Path = f,
                    Layer = r.TryGetProperty("layer", out var l) ? l.GetString() ?? "fg" : "fg",
                    Patterns = r.TryGetProperty("patterns", out var p) && p.ValueKind == JsonValueKind.Object ? p.EnumerateObject().Count() : 0,
                    Rooms = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("rooms", out var rr) ? rr.GetInt32() : 0,
                    Entities = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("entities", out var ee) ? ee.GetInt32() : 0,
                    Sources = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("sources", out var ss) ? ss.GetString() : null,
                    Created = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("created", out var cc) && cc.TryGetDateTime(out var dt) ? dt : null,
                });
            }
            catch (Exception) { /* not a model file */ }
        }
        return list;
    }

    public static PcgModelInfo? CustomModel(string name) =>
        CustomModels().FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>All usable foreground model names: shipped first, then custom fg models.</summary>
    public static List<string> ModelNames() => Models.Concat(CustomModels().Where(m => m.Layer == "fg").Select(m => m.Name)).ToList();

    /// <summary>All usable background model names ("none" first).</summary>
    public static List<string> BackgroundNames() => Backgrounds.Concat(CustomModels().Where(m => m.Layer == "bg").Select(m => m.Name)).ToList();

    public static bool DeleteModel(string name)
    {
        var m = CustomModel(name);
        if (m?.Path == null) return false;
        File.Delete(m.Path);
        return true;
    }

    private static string SafeName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in name.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.Length == 0 ? "custom" : sb.ToString();
    }

    public static PcgModelInfo SaveModel(string name, string layer, TrainResult t, string sources)
    {
        name = SafeName(name);
        if (Models.Contains(name) || Backgrounds.Contains(name)) throw new ArgumentException($"'{name}' is a shipped model name; pick another.");
        Directory.CreateDirectory(CustomModelsDir);
        string path = Path.Combine(CustomModelsDir, name + ".json");
        var file = new JsonObject
        {
            ["name"] = name, ["layer"] = layer,
            ["mask"] = t.Dpe["mask"]?.DeepClone() ?? "000011012",
            ["patterns"] = t.Dpe["patterns"]?.DeepClone() ?? new JsonObject(),
            ["meta"] = new JsonObject
            {
                ["rooms"] = t.RoomsKept, ["entities"] = t.Entities, ["sources"] = sources,
                ["created"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), ["app"] = AppInfo.Title + " " + AppInfo.Version
            }
        };
        File.WriteAllText(path, file.ToJsonString());
        return CustomModel(name)!;
    }

    private static DynValue LoadCustomDpe(Script script, string name, string layer)
    {
        var m = CustomModel(name) ?? throw new ArgumentException($"Unknown {layer} model '{name}'.");
        if (m.Layer != layer) throw new ArgumentException($"Model '{name}' is a {m.Layer} model, not {layer}.");
        var json = JsonNode.Parse(File.ReadAllText(m.Path!)) as JsonObject ?? throw new InvalidDataException("Corrupt model file " + m.Path);
        var t = new Table(script);
        t["mask"] = json["mask"]?.ToString() ?? "000011012";
        t["patterns"] = ToLua(script, json["patterns"]);
        return DynValue.NewTable(t);
    }

    // ---- JSON <-> Lua ----

    internal static DynValue ToLua(Script script, JsonNode? node)
    {
        switch (node)
        {
            case null: return DynValue.Nil;
            case JsonObject o:
            {
                var t = new Table(script);
                foreach (var (k, v) in o) t[k] = ToLua(script, v);
                return DynValue.NewTable(t);
            }
            case JsonArray a:
            {
                var t = new Table(script);
                foreach (var v in a) t.Append(ToLua(script, v));
                return DynValue.NewTable(t);
            }
            case JsonValue v:
                if (v.TryGetValue(out bool b)) return DynValue.NewBoolean(b);
                if (v.TryGetValue(out double d)) return DynValue.NewNumber(d);
                return DynValue.NewString(v.ToString());
            default: return DynValue.NewString(node.ToJsonString());
        }
    }

    internal static JsonNode? ToJson(DynValue v)
    {
        switch (v.Type)
        {
            case DataType.Nil or DataType.Void: return null;
            case DataType.Boolean: return JsonValue.Create(v.Boolean);
            case DataType.Number: return JsonValue.Create(v.Number);
            case DataType.String: return JsonValue.Create(v.String);
            case DataType.Table:
            {
                var t = v.Table;
                if (t.Length > 0 && t.Pairs.All(p => p.Key.Type == DataType.Number))
                {
                    var arr = new JsonArray();
                    for (int i = 1; i <= t.Length; i++) arr.Add(ToJson(t.Get(i)));
                    return arr;
                }
                var o = new JsonObject();
                foreach (var p in t.Pairs) o[p.Key.ToPrintString()] = ToJson(p.Value);
                return o;
            }
            default: return JsonValue.Create(v.ToPrintString());
        }
    }

    // ---- generation ----

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
        script.Globals["__customFg"] = Models.Contains(o.Model) ? DynValue.Nil : LoadCustomDpe(script, o.Model, "fg");
        script.Globals["__customBg"] = o.Background == "none" || Backgrounds.Contains(o.Background) ? DynValue.Nil : LoadCustomDpe(script, o.Background, "bg");

        const string flow = @"
local o = __opts
local Rng = require('library.selecte.rng').Rng
local dpeData = require('library.selecte.dpe_data')
local level = require('library.selecte.level')
local encoder = require('library.selecte.encoder')

local rng = Rng.new(o.seed)
local dpe = __customFg or dpeData[o.model]
if not dpe then error('unknown model ' .. tostring(o.model)) end
local bgDpe = nil
if o.background ~= '' then
  bgDpe = __customBg or dpeData[o.background]
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

    // ---- training (recombine.buildCorpus + mdmc.estimate) ----

    /// <summary>
    /// Estimates a new MdMC model from rooms. Foreground rooms are collapsed so every solid glyph becomes "1" (custom
    /// tilesets included) and the vanilla-named entities are stamped in as symbols; background rooms keep their glyphs,
    /// unknown ones becoming "1". Save the result with <see cref="SaveModel"/>.
    /// </summary>
    public TrainResult Train(IReadOnlyList<CorpusRoom> rooms, string layer, Action<string> log)
    {
        _cancel = false;
        if (rooms.Count == 0) throw new ArgumentException("No rooms to train on.");
        layer = layer == "bg" ? "bg" : "fg";
        var script = CreateScript(log);
        var list = new Table(script);
        foreach (var r in rooms)
        {
            var t = new Table(script);
            t["name"] = r.Name;
            var fg = new Table(script);
            foreach (string row in r.FgRows) fg.Append(DynValue.NewString(row));
            t["fgRows"] = fg;
            if (r.BgRows != null)
            {
                var bg = new Table(script);
                foreach (string row in r.BgRows) bg.Append(DynValue.NewString(row));
                t["bgRows"] = bg;
            }
            var ents = new Table(script);
            foreach (var e in r.Entities)
            {
                var et = new Table(script);
                et["name"] = e.Name; et["x"] = e.X; et["y"] = e.Y;
                if (e.W is { } w) et["width"] = w;
                if (e.H is { } h) et["height"] = h;
                if (e.Red is { } red) et["red"] = red;
                ents.Append(DynValue.NewTable(et));
            }
            t["entities"] = ents;
            list.Append(DynValue.NewTable(t));
        }
        script.Globals["__rooms"] = list;
        script.Globals["__layer"] = layer;
        const string flow = @"
local recombine = require('library.selecte.recombine')
local mdmc = require('library.selecte.mdmc')
local alphabet = require('library.selecte.alphabet')
local grids, summary = recombine.buildCorpus(__rooms, __layer)
if #grids == 0 then error('no usable room in the corpus (all skipped)') end
__log(string.format('seleCte train: %d of %d rooms usable for the %s layer, %d entities stamped', summary.kept, summary.total, __layer, summary.entities or 0))
local vocab = __layer == 'bg' and alphabet.VALID_BG_TILES or alphabet.ALL_TILES_AND_ENTITIES
local dpe, n = mdmc.estimate(grids, alphabet.DEFAULT_MASK, vocab)
dpe._order = nil
local skipped = {}
for _, s in ipairs(summary.skipped or {}) do
  local inv = {}
  for sym, c in pairs(s.invalid or {}) do inv[#inv + 1] = tostring(sym) .. 'x' .. tostring(c) end
  skipped[#skipped + 1] = tostring(s.name) .. (#inv > 0 and (' (' .. table.concat(inv, ' ') .. ')') or '')
end
return { dpe = dpe, rooms = n, patterns = mdmc.patternCount(dpe), kept = summary.kept, total = summary.total, entities = summary.entities or 0, skipped = skipped }
";
        Table res = script.DoString(flow).Table;
        var result = new TrainResult
        {
            Dpe = ToJson(res.Get("dpe")) as JsonObject ?? new JsonObject(),
            Patterns = I(res, "patterns"), RoomsKept = I(res, "kept"), RoomsTotal = I(res, "total"), Entities = I(res, "entities"),
        };
        foreach (var s in Seq(res.Get("skipped"))) result.Skipped.Add(s.ToPrintString());
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
