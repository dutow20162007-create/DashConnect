using DashConnect.Core.Config;
using DashConnect.Core.Logging;

namespace DashConnect.Core.Zapret;

/// <summary>
/// Keeps Zapret's own service state file (utils\game_filter.enabled — the same one service.bat
/// reads) in sync with the mode the app launches winws with, so both stay consistent.
/// </summary>
public static class ZapretSettings
{
    public static void ApplyGameFilter(string root, GameFilterMode mode)
    {
        try
        {
            var dir = Path.Combine(root, "utils");
            if (!Directory.Exists(dir)) return;
            var file = Path.Combine(dir, "game_filter.enabled");

            if (mode == GameFilterMode.Disabled)
            {
                if (File.Exists(file)) File.Delete(file);
            }
            else
            {
                var value = mode switch
                {
                    GameFilterMode.Tcp => "tcp",
                    GameFilterMode.Udp => "udp",
                    _ => "all",
                };
                File.WriteAllText(file, value);
            }
            Log.Info("zapret", $"игровой фильтр Zapret = {mode}");
        }
        catch (Exception ex)
        {
            Log.Warn("zapret", $"game_filter.enabled: {ex.Message}");
        }
    }
}
