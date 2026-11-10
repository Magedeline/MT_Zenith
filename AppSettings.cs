using System.Text.Json;

namespace CELMapViewer;

/// <summary>Small persisted preferences (recent files, view toggles) in %APPDATA%\MT_Zenith\settings.json.</summary>
public sealed class AppSettings
{
    public List<string> RecentFiles { get; set; } = new();
    public bool ShowMinimap { get; set; } = true;
    /// <summary>UI theme id (see <see cref="Themes.All"/>) or "system" to follow the Windows light/dark setting.</summary>
    public string Theme { get; set; } = Themes.SystemId;
    /// <summary>Celeste install folder for standalone (downloaded) builds; null = auto-detect.</summary>
    public string? CelesteDir { get; set; }

    /// <summary>
    /// Room-list group merges per map file name (without extension): name prefix → group it is listed under.
    /// Display only; room names in the map are untouched. Use "*" as the map key to apply to every map.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> RoomGroupMerges { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["08_Truth"] = new(StringComparer.OrdinalIgnoreCase) { ["boss"] = "b" },
    };

    // AI: MCP server + ACP agent chat.
    public int McpPort { get; set; } = 32271;
    public bool McpAutoStart { get; set; }
    public bool AiReadOnly { get; set; }
    public string? LastAgent { get; set; }
    public List<AgentPreset> AgentPresets { get; set; } = AgentPreset.Defaults();
    /// <summary>Preferred ACP session options, re-applied after connecting when the agent offers a matching value.</summary>
    public string? AgentModel { get; set; }
    public string? AgentEffort { get; set; }
    public string? AgentMode { get; set; }
    public bool? AgentFast { get; set; }
    public bool? AgentThinking { get; set; }
    public bool ShowThoughts { get; set; } = true;
    /// <summary>How the agent panel answers ACP permission requests: ask | map (auto-allow MT_Zenith map tools) | all.</summary>
    public string AgentPermissions { get; set; } = "ask";
    /// <summary>Strongest-effort mode: max model/effort, autonomy directive in every prompt, auto-continue on turn limits.</summary>
    public bool AgentMaxEffort { get; set; }
    /// <summary>PCGHelper folder chosen with PCG ▸ Locate PCGHelper (its Loenn folder or the mod root).</summary>
    public string? PcgHelperDir { get; set; }

    private static string FilePath => Path.Combine(AppInfo.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt settings: start fresh */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not critical */ }
    }

    public void AddRecent(string path)
    {
        path = Path.GetFullPath(path);
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
        Save();
    }
}

/// <summary>An ACP agent command line (run from the mod folder through cmd.exe).</summary>
public sealed class AgentPreset
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";

    public override string ToString() => Name;

    public static List<AgentPreset> Defaults() => new()
    {
        new() { Name = "Claude Code", Command = "npx -y @agentclientprotocol/claude-agent-acp" },
        new() { Name = "OpenAI Codex", Command = "npx -y @agentclientprotocol/codex-acp" },
        new() { Name = "GitHub Copilot CLI", Command = "copilot --acp" },
        new() { Name = "Gemini CLI", Command = "gemini --experimental-acp" },
        new() { Name = "OpenCode (Grok, any provider)", Command = "opencode acp" },
        new() { Name = "Goose", Command = "goose acp" },
    };
}
