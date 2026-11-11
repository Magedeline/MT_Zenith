using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

// Credits: who made what the map uses (installed mods via Maddie480's GameBanana database, Asset Drive, own work).
public sealed partial class MapTools
{
    private void RegisterCredits()
    {
        Add("asset_credits", "Who to credit for everything the open map uses: decals, stylegrounds, tilesets and entity/trigger types. " +
            "Installed mods are matched to their GameBanana page and author through Maddie480's mod database (maddie480.ovh); files in the " +
            "map's own mod use the credits recorded in MT_Zenith.credits.json, and unrecorded ones are matched by name against the Celeste " +
            "Community Asset Drive. Returns a ready credits.txt plus a list of assets that still need a credit decision. " +
            "When adding decals or stylegrounds from other mods or the Asset Drive, run this afterwards and tell the user who to credit.", true,
            Props(("refresh", P("boolean", "Re-download Maddie480's databases instead of using the daily cache (default false).")),
                  ("offline", P("boolean", "Use only cached data, no downloads (default false).")),
                  ("details", P("boolean", "Include every entry, not just the credits text and the TODO list (default false).")),
                  ("recommend", P("integer", "Also return this many Celeste Community Asset Drive files that suit the map (matched on its decal, " +
                      "styleground and room-name themes and on creators it already credits), with why and who to credit (default 0)."))), None, AssetCreditsTool);
    }

    private ToolResult AssetCreditsTool(JsonObject a)
    {
        var (map, path) = _host.Invoke(() => _host.Map == null ? (null, null)
            : (new MapBin { Package = _host.Map.Package, Root = _host.Map.Root.Clone() }, _host.Path));
        if (map == null) throw new ToolException("No map is open.");
        bool refresh = a["refresh"]?.GetValue<bool>() ?? false, offline = a["offline"]?.GetValue<bool>() ?? false;
        var r = AssetCredits.ScanAsync(map, path, !offline, refresh).GetAwaiter().GetResult();
        string name = path == null ? "Untitled map" : System.IO.Path.GetFileNameWithoutExtension(path);
        var o = new JsonObject
        {
            ["mod_folder"] = r.ModDir,
            ["online_data"] = r.OnlineData,
            ["credits_txt"] = AssetCredits.CreditsText(r, name),
        };
        var todo = new JsonArray();
        foreach (var e in r.Entries.Where(e => e.NeedsAttention)) todo.Add(Entry(e));
        o["needs_credit"] = todo;
        if (a["details"]?.GetValue<bool>() == true)
        {
            var all = new JsonArray();
            foreach (var e in r.Entries) all.Add(Entry(e));
            o["entries"] = all;
        }
        int rec = a["recommend"]?.GetValue<int>() ?? 0;
        if (rec > 0 && !offline)
        {
            var authors = new HashSet<string>(r.Entries.Where(e => e.Origin == CreditOrigin.Borrowed).Select(e => e.CreditTo), StringComparer.OrdinalIgnoreCase);
            var picks = CreditAdvice.Recommend(MaddieDb.DriveAllAsync().GetAwaiter().GetResult(), CreditAdvice.MapKeywords(map), authors, Math.Min(rec, 100));
            var arr = new JsonArray();
            foreach (var (d, score, why) in picks)
                arr.Add(new JsonObject
                {
                    ["name"] = d.Name, ["category"] = d.Category, ["author"] = d.CleanAuthor, ["why"] = why,
                    ["score"] = Math.Round(score, 2), ["size"] = $"{d.Width}x{d.Height}", ["image_url"] = d.FileUrl, ["notes"] = d.Notes,
                });
            o["recommended_assets"] = arr;
        }
        return ToolResult.Json(o);

        static JsonObject Entry(CreditEntry e) => new()
        {
            ["kind"] = e.Kind, ["asset"] = e.Asset, ["origin"] = e.Origin.ToString(), ["credit_to"] = e.CreditTo,
            ["source"] = e.Source, ["url"] = e.Url, ["uses"] = e.Uses, ["note"] = e.Note, ["next_step"] = CreditAdvice.NextStep(e), ["file"] = e.File,
        };
    }
}
