using System.IO.Compression;
using System.Text.RegularExpressions;

namespace CELMapViewer;

/// <summary>
/// Lönn-style project manager: a project is a loose mod folder (Mods/&lt;Mod&gt;) with maps under
/// Maps/&lt;user&gt;/&lt;campaign&gt;/&lt;map&gt;.bin. Creates empty or standard projects, lists campaigns and maps, zips the mod.
/// </summary>
public static class ProjectManager
{
    private static readonly Regex SafeName = new(@"[^A-Za-z0-9_\-]", RegexOptions.Compiled);

    public static string Clean(string s) => SafeName.Replace(s.Trim().Replace(' ', '_'), "");

    /// <summary>Campaign folder (Maps/user/campaign, relative) → maps in it.</summary>
    public static SortedDictionary<string, List<string>> Campaigns(string projectDir)
    {
        var result = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string maps = Path.Combine(projectDir, "Maps");
        if (!Directory.Exists(maps)) return result;
        foreach (string bin in Directory.EnumerateFiles(maps, "*.bin", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string campaign = Path.GetRelativePath(maps, Path.GetDirectoryName(bin)!).Replace('\\', '/');
            if (campaign == ".") campaign = "(root)";
            if (!result.TryGetValue(campaign, out var list)) result[campaign] = list = new();
            list.Add(bin);
        }
        return result;
    }

    /// <summary>
    /// Creates Mods/&lt;mod&gt; with everest.yaml and a first map holding one room with a floor and a spawn.
    /// A standard project also gets dialog keys, decal/styleground/checkpoint folders and a copy of the vanilla tileset xmls
    /// wired up through the map's meta.yaml. Returns the new map path.
    /// </summary>
    public static string Create(string modsDir, string mod, string user, string campaign, string mapName, bool standard)
    {
        mod = Clean(mod); user = Clean(user); campaign = Clean(campaign); mapName = Clean(mapName);
        if (mod.Length == 0 || user.Length == 0 || campaign.Length == 0 || mapName.Length == 0)
            throw new ArgumentException("Mod, user, campaign and map names need at least one letter or digit.");
        string root = Path.Combine(modsDir, mod);
        string mapDir = Path.Combine(root, "Maps", user, campaign);
        string mapPath = Path.Combine(mapDir, mapName + ".bin");
        if (File.Exists(mapPath)) throw new IOException($"{mapPath} already exists.");
        Directory.CreateDirectory(mapDir);

        string yaml = Path.Combine(root, "everest.yaml");
        if (!File.Exists(yaml))
            File.WriteAllText(yaml, $"- Name: {mod}\n  Version: 1.0.0\n  Dependencies:\n    - Name: EverestCore\n      Version: 1.4465.0\n");

        var map = NewMap($"{user}/{campaign}/{mapName}");
        map.Save(mapPath);

        if (standard)
        {
            string key = $"{user}_{campaign}";
            string dialog = Path.Combine(root, "Dialog", "English.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(dialog)!);
            var lines = File.Exists(dialog) ? File.ReadAllLines(dialog).ToList() : new List<string> { "# Map and chapter names" };
            void Key(string k, string v) { if (!lines.Any(l => l.StartsWith(k + "=", StringComparison.Ordinal))) lines.Add($"{k}= {v}"); }
            Key(key, campaign.Replace('_', ' '));
            Key($"{key}_{mapName}", mapName.Replace('_', ' '));
            File.WriteAllLines(dialog, lines);

            foreach (string dir in new[] { "Graphics/Atlases/Gameplay/decals", "Graphics/Atlases/Gameplay/bgs", "Graphics/Atlases/Checkpoints", "Graphics/Atlases/Gameplay/tilesets" })
                Directory.CreateDirectory(Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar), user, campaign));

            string xmls = Path.Combine(root, "Graphics", user, campaign, "xmls");
            Directory.CreateDirectory(xmls);
            var meta = new List<string>();
            foreach (var (file, metaKey) in new[] { ("ForegroundTiles.xml", "ForegroundTiles"), ("BackgroundTiles.xml", "BackgroundTiles"), ("AnimatedTiles.xml", "AnimatedTiles") })
            {
                string dest = Path.Combine(xmls, file);
                if (!File.Exists(dest) && GameAssets.VanillaFile("Graphics/" + file) is { } src && File.Exists(src)) File.Copy(src, dest);
                if (File.Exists(dest)) meta.Add($"{metaKey}: Graphics/{user}/{campaign}/xmls/{file}");
            }
            string metaPath = Path.Combine(mapDir, mapName + ".meta.yaml");
            if (!File.Exists(metaPath) && meta.Count > 0) File.WriteAllLines(metaPath, meta);
        }
        return mapPath;
    }

    /// <summary>A map with the elements Lönn writes and one 40x23 room with a floor and a spawn point.</summary>
    public static MapBin NewMap(string package)
    {
        var map = new MapBin { Package = package, Root = new BinElement("Map") };
        var levels = MapModel.Levels(map);
        var floor = new TileGrid(40, 23);
        for (int y = 20; y < 23; y++) for (int x = 0; x < 40; x++) floor[x, y] = '1';
        var level = MapModel.NewLevel("a-00", 0, 0, 320, 184, floor.Serialize());
        var player = new BinElement("player");
        player.Set("id", 1); player.Set("x", 32); player.Set("y", 160);
        level.Child("entities")!.Children.Add(player);
        levels.Children.Add(level);
        map.Root.Children.Add(new BinElement("Filler"));
        var style = new BinElement("Style");
        style.Children.Add(new BinElement("Foregrounds"));
        style.Children.Add(new BinElement("Backgrounds"));
        map.Root.Children.Add(style);
        return map;
    }

    /// <summary>Zips a mod folder (skipping .git, .vs, bin/obj and temp files) the way Everest loads it.</summary>
    public static int Zip(string projectDir, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        int count = 0;
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (string file in Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
            var parts = rel.Split('/');
            if (parts.Any(p => p is ".git" or ".vs" or ".vscode" or ".idea" or "obj") || (parts[0] == "bin" && parts.Length > 1)) continue;
            if (rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFullPath(file).Equals(Path.GetFullPath(zipPath), StringComparison.OrdinalIgnoreCase)) continue;
            zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
            count++;
        }
        return count;
    }
}
