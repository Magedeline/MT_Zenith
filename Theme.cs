using Microsoft.Win32;

namespace CELMapViewer;

/// <summary>A UI color theme: window chrome colors plus the map canvas colors.</summary>
public sealed record Theme(
    string Id, string Name, string Group, bool Dark,
    Color Back,          // forms, panels, tab pages
    Color Surface,       // text boxes, lists, trees, grids
    Color Raised,        // menus, toolbars, status bar, buttons, inactive tabs
    Color Text,
    Color Muted,         // hints, secondary labels
    Color Accent,        // primary highlight (selected tab bar, "You:" in chat, links)
    Color Accent2,       // secondary highlight ("Agent:" in chat)
    Color Border,
    Color Selection,     // selected item background
    Color SelectionText,
    Color Canvas,        // map canvas background
    Color RoomFill,
    Color RoomBorder,
    Color RoomSelect)
{
    /// <summary>Tool-call lines in the agent chat and other warnings.</summary>
    public Color Warn => Dark ? Color.FromArgb(255, 176, 80) : Color.FromArgb(200, 110, 0);
    public Color Error => Dark ? Color.FromArgb(255, 110, 110) : Color.Firebrick;
    public override string ToString() => Name;
}

/// <summary>Built-in themes, the current theme and its change notification.</summary>
public static class Themes
{
    public const string SystemId = "system";

    private static Color H(string hex) => ColorTranslator.FromHtml("#" + hex);

    private static Theme T(string id, string name, string group, bool dark,
        string back, string surface, string raised, string text, string muted, string accent, string accent2,
        string border, string sel, string selText, string canvas, string roomFill, string roomBorder, string roomSel) =>
        new(id, name, group, dark, H(back), H(surface), H(raised), H(text), H(muted), H(accent), H(accent2),
            H(border), H(sel), H(selText), H(canvas), H(roomFill), H(roomBorder), H(roomSel));

    public static readonly IReadOnlyList<Theme> All = new[]
    {
        //   id                name                     group      dark   back      surface   raised    text      muted     accent    accent2   border    select    selText   canvas    roomFill  roomBord  roomSel
        T("light",          "Light",                 "Basic",   false, "F0F0F0", "FFFFFF", "F7F7F7", "1A1A1A", "696969", "0063B1", "2E8B57", "C8C8C8", "CCE4F7", "1A1A1A", "18181E", "2C2C36", "6E6E82", "78C8FF"),
        T("dark",           "Dark",                  "Basic",   true,  "252526", "1E1E1E", "2D2D30", "E6E6E6", "9A9A9A", "3794FF", "4EC9B0", "3F3F46", "094771", "FFFFFF", "18181E", "2C2C36", "6E6E82", "78C8FF"),
        T("midnight",       "Midnight (OLED)",       "Basic",   true,  "0A0A0C", "000000", "141418", "DCDCE0", "8A8A94", "5AA9FF", "5FD7A5", "26262C", "1C3554", "FFFFFF", "000000", "16161C", "4A4A5A", "78C8FF"),
        T("catppuccin-latte","Catppuccin Latte",     "Basic",   false, "EFF1F5", "FFFFFF", "E6E9EF", "4C4F69", "6C6F85", "8839EF", "1E66F5", "CCD0DA", "DCE0E8", "4C4F69", "1E1E2E", "313244", "6C7086", "89B4FA"),
        T("catppuccin-mocha","Catppuccin Mocha",     "Basic",   true,  "1E1E2E", "181825", "313244", "CDD6F4", "A6ADC8", "CBA6F7", "89B4FA", "45475A", "45475A", "CDD6F4", "11111B", "313244", "6C7086", "89B4FA"),

        // Celeste: night on the mountain, Madeline's red hair and her blue (dashless) hair.
        T("celeste",        "Celeste (Night)",       "Celeste", true,  "1B1830", "141226", "241F3F", "EDE7FF", "9C95C0", "E0465A", "44B7FF", "3B3463", "3A2F6B", "FFFFFF", "120F22", "262042", "6C61A8", "44B7FF"),
        T("celeste-summit", "Celeste (Summit)",      "Celeste", false, "EEF1F8", "FFFFFF", "E2E7F3", "1E2140", "5F6585", "AC3232", "2E7FD6", "C3CBE0", "D3DCF2", "1E2140", "1C2038", "2E3454", "8790C0", "44B7FF"),
        T("farewell",       "Celeste (Farewell)",    "Celeste", true,  "10142A", "0B0E1F", "1A1F3D", "E8ECFF", "9AA3CC", "F28BC2", "5DE0E6", "2C3360", "3A2A5E", "FFFFFF", "080A18", "1A1F3D", "5A63A0", "5DE0E6"),

        // Mods.
        T("desolo-zantas",  "Desolo Zantas",         "Mods",    true,  "1C0F26", "140A1C", "261436", "F0D8F0", "A88FB4", "C060E0", "E8A0F0", "3E2252", "481860", "FFFFFF", "120818", "24132F", "7A4A96", "E8A0F0"),
        T("strawberry-jam", "Strawberry Jam",        "Mods",    true,  "2A0A16", "1F0711", "3A0E20", "FFE4EC", "D49AAE", "F07878", "F0A8C0", "5A1A32", "7A1838", "FFFFFF", "1A060E", "34101F", "A03A5C", "F0A8C0"),
        T("spring-collab",  "Spring Collab 2020",    "Mods",    false, "EEF6E8", "FBFFF7", "DDEFD2", "1F3320", "5E7A5C", "D9558A", "3E8E3E", "B9D6AC", "F7D3E2", "1F3320", "1F2A22", "2E3F31", "7FB27A", "F7A8C8"),
        T("gameboy",        "Game Boy",              "Mods",    false, "8BAC0F", "9BBC0F", "8BAC0F", "0F380F", "306230", "0F380F", "306230", "306230", "306230", "9BBC0F", "0F380F", "306230", "8BAC0F", "9BBC0F"),
        T("gamebanana",     "GameBanana",            "Mods",    true,  "1C1F26", "15171C", "252932", "E8E8E8", "9AA0AA", "FFB300", "7CC4FF", "343944", "4A3A10", "FFFFFF", "121418", "252932", "6A7080", "FFB300"),
    };

    /// <summary>The setting value currently chosen (a theme id or <see cref="SystemId"/>).</summary>
    public static string Selected { get; private set; } = SystemId;
    public static Theme Current { get; private set; } = All[0];

    /// <summary>Raised after <see cref="Current"/> changes (old theme, new theme).</summary>
    public static event Action<Theme, Theme>? Changed;

    public static Theme? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Light or Dark following the Windows "app mode" setting.</summary>
    public static Theme FromSystem()
    {
        bool light = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) light = v != 0;
        }
        catch { /* default light */ }
        return Find(light ? "light" : "dark")!;
    }

    private static bool _watchingSystem;

    public static void Select(string? id)
    {
        Selected = Find(id) != null ? Find(id)!.Id : SystemId;
        if (Selected == SystemId && !_watchingSystem)
        {
            _watchingSystem = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (Selected == SystemId && e.Category == UserPreferenceCategory.General) SetCurrent(FromSystem());
            };
        }
        SetCurrent(Selected == SystemId ? FromSystem() : Find(Selected)!);
    }

    private static void SetCurrent(Theme t)
    {
        if (t == Current) return;
        var old = Current;
        Current = t;
        Changed?.Invoke(old, t);
    }

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}
