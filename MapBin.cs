using System.Globalization;
using System.Text;

namespace CELMapViewer;

/// <summary>Attribute value type ids used by Celeste.BinaryPacker.</summary>
public enum BinType : byte
{
    Bool = 0, Byte = 1, Short = 2, Int = 3, Float = 4, Lookup = 5, String = 6, Rle = 7
}

public sealed class BinAttr
{
    public string Key;
    public BinType Type;
    public object Value;

    public BinAttr(string key, BinType type, object value) { Key = key; Type = type; Value = value; }
    public BinAttr Clone() => new(Key, Type, Value);
}

/// <summary>
/// One BinaryPacker element. Attribute order and value types are kept exactly as read,
/// so an unedited map writes back byte-for-byte.
/// </summary>
public sealed class BinElement
{
    public string Name;
    public List<BinAttr> Attrs = new();
    public List<BinElement> Children = new();

    public BinElement(string name) { Name = name; }

    public BinAttr? Find(string key) => Attrs.Find(a => a.Key == key);
    public BinElement? Child(string name) => Children.Find(c => c.Name == name);

    public string Str(string key, string def = "") => Find(key)?.Value?.ToString() ?? def;

    public float Num(string key, float def = 0)
    {
        object? v = Find(key)?.Value;
        return v switch
        {
            int i => i,
            float f => f,
            bool b => b ? 1 : 0,
            string s when float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float p) => p,
            _ => def
        };
    }

    public int Int(string key, int def = 0) => (int)MathF.Round(Num(key, def));

    /// <summary>Sets a value, keeping the existing type where the new value fits it.</summary>
    public void Set(string key, object value)
    {
        BinAttr? a = Find(key);
        BinType type = MapBin.TypeFor(value, a?.Type);
        object stored = MapBin.Coerce(value, type);
        if (a == null) Attrs.Add(new BinAttr(key, type, stored));
        else { a.Type = type; a.Value = stored; }
    }

    public bool Remove(string key) => Attrs.RemoveAll(a => a.Key == key) > 0;

    public BinElement Clone()
    {
        var e = new BinElement(Name);
        foreach (var a in Attrs) e.Attrs.Add(a.Clone());
        foreach (var c in Children) e.Children.Add(c.Clone());
        return e;
    }

    public override string ToString() => Name;
}

public sealed class MapBin
{
    public const string Header = "CELESTE MAP";

    public string Package = "";
    public BinElement Root = new("Map");
    /// <summary>The string table as read, so re-saving keeps its order.</summary>
    public List<string> Lookup = new();

    public static MapBin Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Load(fs, path);
    }

    /// <summary>Reads a map from any stream (e.g. a zip entry). <paramref name="name"/> is only used in errors.</summary>
    public static MapBin Load(Stream stream, string name)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadString() != Header)
            throw new InvalidDataException("Not a Celeste map: " + name);

        var map = new MapBin { Package = r.ReadString() };
        int count = r.ReadInt16();
        for (int i = 0; i < count; i++) map.Lookup.Add(r.ReadString());
        map.Root = ReadElement(r, map.Lookup);
        return map;
    }

    private static BinElement ReadElement(BinaryReader r, List<string> lookup)
    {
        var e = new BinElement(lookup[r.ReadInt16()]);
        int attrs = r.ReadByte();
        for (int i = 0; i < attrs; i++)
        {
            string key = lookup[r.ReadInt16()];
            var type = (BinType)r.ReadByte();
            object value = type switch
            {
                BinType.Bool => r.ReadBoolean(),
                BinType.Byte => (int)r.ReadByte(),
                BinType.Short => (int)r.ReadInt16(),
                BinType.Int => r.ReadInt32(),
                BinType.Float => r.ReadSingle(),
                BinType.Lookup => lookup[r.ReadInt16()],
                BinType.String => r.ReadString(),
                BinType.Rle => RleDecode(r.ReadBytes(r.ReadInt16())),
                _ => throw new InvalidDataException($"Unknown attribute type {(byte)type} on {e.Name}.{key}")
            };
            e.Attrs.Add(new BinAttr(key, type, value));
        }
        int children = r.ReadInt16();
        for (int i = 0; i < children; i++) e.Children.Add(ReadElement(r, lookup));
        return e;
    }

    public void Save(string path)
    {
        var lookup = new List<string>(Lookup);
        var index = new Dictionary<string, short>();
        for (int i = 0; i < lookup.Count; i++) index.TryAdd(lookup[i], (short)i);
        Collect(Root, lookup, index);
        if (lookup.Count > short.MaxValue)
            throw new InvalidDataException("Too many distinct strings for the map format.");

        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var w = new BinaryWriter(fs, Encoding.UTF8))
        {
            w.Write(Header);
            w.Write(Package);
            w.Write((short)lookup.Count);
            foreach (string s in lookup) w.Write(s);
            WriteElement(w, Root, index);
        }
        File.Move(tmp, path, overwrite: true);
        Lookup = lookup;
    }

    private static void Collect(BinElement e, List<string> lookup, Dictionary<string, short> index)
    {
        void Add(string s)
        {
            if (index.ContainsKey(s)) return;
            index[s] = (short)lookup.Count;
            lookup.Add(s);
        }
        Add(e.Name);
        foreach (var a in e.Attrs)
        {
            Add(a.Key);
            if (a.Type == BinType.Lookup) Add(a.Value?.ToString() ?? "");
        }
        foreach (var c in e.Children) Collect(c, lookup, index);
    }

    private static void WriteElement(BinaryWriter w, BinElement e, Dictionary<string, short> index)
    {
        if (e.Attrs.Count > byte.MaxValue)
            throw new InvalidDataException($"Element {e.Name} has more than 255 attributes.");
        w.Write(index[e.Name]);
        w.Write((byte)e.Attrs.Count);
        foreach (var a in e.Attrs)
        {
            w.Write(index[a.Key]);
            BinType type = a.Type;
            byte[]? rle = null;
            if (type == BinType.Rle)
            {
                rle = RleEncode(a.Value?.ToString() ?? "");
                if (rle.Length > short.MaxValue) type = BinType.String;
            }
            w.Write((byte)type);
            switch (type)
            {
                case BinType.Bool: w.Write(Convert.ToBoolean(a.Value)); break;
                case BinType.Byte: w.Write(Convert.ToByte(a.Value)); break;
                case BinType.Short: w.Write(Convert.ToInt16(a.Value)); break;
                case BinType.Int: w.Write(Convert.ToInt32(a.Value)); break;
                case BinType.Float: w.Write(Convert.ToSingle(a.Value, CultureInfo.InvariantCulture)); break;
                case BinType.Lookup: w.Write(index[a.Value?.ToString() ?? ""]); break;
                case BinType.String: w.Write(a.Value?.ToString() ?? ""); break;
                case BinType.Rle: w.Write((short)rle!.Length); w.Write(rle); break;
            }
        }
        w.Write((short)e.Children.Count);
        foreach (var c in e.Children) WriteElement(w, c, index);
    }

    public static string RleDecode(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 4);
        for (int i = 0; i + 1 < bytes.Length; i += 2) sb.Append((char)bytes[i + 1], bytes[i]);
        return sb.ToString();
    }

    public static byte[] RleEncode(string s)
    {
        var list = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            byte n = 1;
            char c = s[i];
            while (i + 1 < s.Length && s[i + 1] == c && n < byte.MaxValue) { n++; i++; }
            list.Add(n);
            list.Add((byte)c);
        }
        return list.ToArray();
    }

    /// <summary>Picks a storage type for a value, preferring the attribute's previous type.</summary>
    public static BinType TypeFor(object value, BinType? previous)
    {
        switch (value)
        {
            case bool: return BinType.Bool;
            case float or double: return BinType.Float;
            case int i:
                if (previous == BinType.Float) return BinType.Float;
                if (previous == BinType.Byte && i is >= 0 and <= 255) return BinType.Byte;
                if (previous == BinType.Short && i is >= short.MinValue and <= short.MaxValue) return BinType.Short;
                if (previous is BinType.Byte or BinType.Short or BinType.Int) return BinType.Int;
                return i is >= short.MinValue and <= short.MaxValue ? (i is >= 0 and <= 255 ? BinType.Byte : BinType.Short) : BinType.Int;
            default:
                return previous is BinType.Lookup or BinType.Rle ? previous.Value : BinType.String;
        }
    }

    public static object Coerce(object value, BinType type) => type switch
    {
        BinType.Bool => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
        BinType.Byte or BinType.Short or BinType.Int => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        BinType.Float => Convert.ToSingle(value, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    /// <summary>Parses text typed into the attribute grid using the attribute's current type as a hint.</summary>
    public static object ParseText(string text, BinType? hint)
    {
        text = text.Trim();
        if (hint is BinType.String or BinType.Lookup or BinType.Rle) return text;
        if (bool.TryParse(text, out bool b)) return b;
        if (hint != BinType.Float && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return f;
        return text;
    }
}
