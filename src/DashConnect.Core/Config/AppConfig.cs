namespace DashConnect.Core.Config;

/// <summary>Game-filter mode for Zapret (whether winws also desyncs high game ports).</summary>
public enum GameFilterMode { Disabled, All, Tcp, Udp }

/// <summary>Persisted user settings. Serialized to %AppData%\DashConnect\config.json.</summary>
public sealed class AppConfig
{
    /// <summary>Root folder of the Zapret distribution (contains bin\, lists\, *.bat).</summary>
    public string ZapretRoot { get; set; } =
        @"C:\Users\HU9O\Desktop\zapret-discord-youtube\zapret-discord-youtube-1.9.9c";

    // ---- DPI bypass (Zapret / winws) ----

    public bool DpiBypassEnabled { get; set; } = true;

    /// <summary>Route games through Zapret DPI by desyncing game ports (heavier — off by default,
    /// it intercepts high ports and can disturb other traffic on some networks).</summary>
    public bool GameDpiEnabled { get; set; }

    /// <summary>
    /// When true, probe several presets and pick the best (slower, disturbs the network while
    /// testing). When false (default) a single preset is launched directly — like double-clicking
    /// the .bat — which is fast and cannot freeze.
    /// </summary>
    /// <summary>
    /// ON by default: probe all presets one by one (custom first) and keep the first that works.
    /// Each preset fully resets the WinDivert driver; the UI-log exception loop that used to freeze
    /// the window during the sweep is fixed. OFF launches one strong preset directly.
    /// </summary>
    public bool AutoSelect { get; set; } = true;

    /// <summary>Manual override: launch this exact preset. Null → the default strong preset.</summary>
    public string? PreferredStrategy { get; set; }

    /// <summary>
    /// Switch the system to encrypted DNS (Cloudflare DoH) while connected and back to automatic
    /// on disconnect. Defeats DNS poisoning — the blocking layer DPI desync can't reach. On by default.
    /// </summary>
    public bool CleanDnsEnabled { get; set; } = true;

    // ---- VPN (sing-box tunnel via subscription) ----

    /// <summary>Enable the sing-box VPN tunnel (routes traffic through the selected server).</summary>
    public bool VpnEnabled { get; set; }

    /// <summary>true = ТУННЕЛЬ (route everything); false = ПРОКСИ (smart split, lowest ping).</summary>
    public bool VpnFull { get; set; }

    /// <summary>Subscription URL, or a single vless://ss://vmess://trojan:// link.</summary>
    public string SubscriptionUrl { get; set; } = "";

    /// <summary>Name of the profile selected from the subscription.</summary>
    public string? SelectedProfileName { get; set; }

    // ---- App ----

    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>App version this config was written by. On mismatch, scan settings are reset.</summary>
    public string AppVersion { get; set; } = "";
}
