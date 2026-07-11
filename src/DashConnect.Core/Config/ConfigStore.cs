using System.Text.Json;
using System.Text.Json.Serialization;
using DashConnect.Core.Logging;
using DashConnect.Core.Util;

namespace DashConnect.Core.Config;

/// <summary>Loads and saves <see cref="AppConfig"/> with resilient defaults.</summary>
public static class ConfigStore
{
    /// <summary>Bump this on releases so stale scan settings are auto-reset on upgrade.</summary>
    public const string CurrentVersion = "1.8";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
            {
                var json = File.ReadAllText(Paths.ConfigFile);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg is not null)
                {
                    if (cfg.AppVersion != CurrentVersion)
                    {
                        // App was updated — reset scan settings to safe defaults.
                        cfg.AutoSelect = true;
                        cfg.GameDpiEnabled = false; // all-port intercept off (opt back in if wanted)
                        cfg.PreferredStrategy = null;
                        cfg.CleanDnsEnabled = true; // encrypted DNS on by default
                        cfg.AppVersion = CurrentVersion;
                        Save(cfg);
                        Log.Info("config", "новая версия — настройки сканирования сброшены");
                    }
                    else
                    {
                        Log.Info("config", "настройки загружены");
                    }
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("config", $"load failed, using defaults: {ex.Message}");
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Paths.EnsureDirectories();
            var json = JsonSerializer.Serialize(config, Options);
            File.WriteAllText(Paths.ConfigFile, json);
            Log.Debug("config", "saved settings");
        }
        catch (Exception ex)
        {
            Log.Error("config", "save failed", ex);
        }
    }
}
