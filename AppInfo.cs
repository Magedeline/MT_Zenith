using System.Reflection;

namespace CELMapViewer;

/// <summary>Product branding and the per-user data folder (formerly "CEL Map Viewer" / %APPDATA%\CELMapViewer).</summary>
public static class AppInfo
{
    public const string Name = "MT_Zenith";
    public const string Title = "MT_Zenith";
    public const string McpName = "mt-zenith";

    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static string? _dataDir;

    /// <summary>%APPDATA%\MT_Zenith. On first use it copies over the settings and caches from the old %APPDATA%\CELMapViewer.</summary>
    public static string DataDir
    {
        get
        {
            if (_dataDir != null) return _dataDir;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, Name), old = Path.Combine(appData, "CELMapViewer");
            try
            {
                if (!Directory.Exists(dir) && Directory.Exists(old))
                {
                    Directory.CreateDirectory(dir);
                    foreach (string f in Directory.EnumerateFiles(old)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return _dataDir = dir;
        }
    }

    /// <summary>The popstar berry icon embedded in the exe.</summary>
    public static Icon? LoadIcon()
    {
        try
        {
            using var s = typeof(AppInfo).Assembly.GetManifestResourceStream("MT_Zenith.ico");
            return s == null ? null : new Icon(s);
        }
        catch (ArgumentException) { return null; }
    }
}
