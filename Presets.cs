using System.Text.Json;
using System.Text.Json.Serialization;

namespace CELMapViewer;

/// <summary>
/// Saved entity/trigger presets and preset groups (several entities, triggers and decals placed together
/// with their relative positions), kept in %APPDATA%\MT_Zenith\presets.json.
/// </summary>
public sealed class Preset
{
    /// <summary>"entity", "trigger" or "group".</summary>
    public string Kind { get; set; } = "group";
    public string Name { get; set; } = "";
    /// <summary>Items in <see cref="ItemClipboard"/> format, positions relative to the group's top-left.</summary>
    public string Items { get; set; } = "";

    [JsonIgnore] public string KindLabel => Kind switch { "entity" => "Entities", "trigger" => "Triggers", _ => "Preset Groups" };
}

public static class PresetStore
{
    private static string FilePath => Path.Combine(AppInfo.DataDir, "presets.json");
    private static List<Preset>? _all;

    public static List<Preset> All => _all ??= Load();

    private static List<Preset> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt: start empty */ }
        return new();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not critical */ }
    }

    /// <summary>Builds a preset from items in room-local or world space; positions are stored relative to their top-left.</summary>
    public static Preset Make(string name, IReadOnlyList<(string Layer, BinElement El)> items, bool forceGroup = false)
    {
        float minX = items.Min(i => i.El.Num("x")), minY = items.Min(i => i.El.Num("y"));
        var copies = items.Select(i =>
        {
            var c = i.El.Clone();
            Shift(c, -minX, -minY);
            c.Remove("id");
            return (i.Layer, c);
        }).ToList();
        string kind = forceGroup || copies.Count != 1 ? "group" : copies[0].Layer == "trigger" ? "trigger" : copies[0].Layer == "entity" ? "entity" : "group";
        return new Preset { Name = name, Kind = kind, Items = ItemClipboard.Serialize(copies) };
    }

    public static void Add(Preset p)
    {
        All.RemoveAll(x => x.Kind == p.Kind && string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        All.Add(p);
        Save();
    }

    public static List<(string Layer, BinElement El)>? Items(Preset p) => ItemClipboard.Deserialize(p.Items);

    private static void Shift(BinElement el, float dx, float dy)
    {
        MapCanvas.SetNum(el, "x", el.Num("x") + dx);
        MapCanvas.SetNum(el, "y", el.Num("y") + dy);
        foreach (var n in MapCanvas.Nodes(el)) { MapCanvas.SetNum(n, "x", n.Num("x") + dx); MapCanvas.SetNum(n, "y", n.Num("y") + dy); }
    }
}
