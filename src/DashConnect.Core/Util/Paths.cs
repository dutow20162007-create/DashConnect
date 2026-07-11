namespace DashConnect.Core.Util;

/// <summary>Canonical file-system locations used across the app.</summary>
public static class Paths
{
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DashConnect");

    public static string ConfigFile => Path.Combine(AppDataDir, "config.json");
    public static string LogsDir => Path.Combine(AppDataDir, "logs");
    public static string LogFile => Path.Combine(LogsDir, "dashconnect.log");

    /// <summary>Directory that holds the executable (assets live alongside it).</summary>
    public static string BaseDir { get; } = AppContext.BaseDirectory;

    public static string AssetsDir => Path.Combine(BaseDir, "assets");
    public static string SingboxDir => Path.Combine(AppDataDir, "singbox");
    public static string SingboxExe => Path.Combine(SingboxDir, "sing-box.exe");
    public static string SingboxConfig => Path.Combine(SingboxDir, "config.json");
    public static string GameRoutesFile => Path.Combine(AppDataDir, "game-routes.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(SingboxDir);
    }
}
