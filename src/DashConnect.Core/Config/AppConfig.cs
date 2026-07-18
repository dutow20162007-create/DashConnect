namespace DashConnect.Core.Config;

/// <summary>Game-filter mode for Zapret (whether winws also desyncs high game ports).</summary>
public enum GameFilterMode { Disabled, All, Tcp, Udp }

/// <summary>Which engine powers the VPN toggle.</summary>
public enum VpnKind
{
    /// <summary>sing-box tunnel from a vless://…/subscription link (smart-split or full).</summary>
    Singbox,
    /// <summary>AmneziaWG (obfuscated WireGuard) from a pasted .conf — always a full tunnel.</summary>
    Amnezia,
}

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
    /// Low-ping mode for games. winws normally captures wide UDP/TCP port ranges (50000-65535 for
    /// Discord voice, 1024-65535 with the game filter): WinDivert then diverts EVERY packet on those
    /// ports into userspace and reinjects it, which is what added latency/jitter in games. With this
    /// on, those wide ranges are stripped from the capture filter — games stop being touched at all.
    /// Trade-off: Discord VOICE may stop working (chat/YouTube are unaffected).
    /// </summary>
    public bool LowPingMode { get; set; }

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

    // ---- VPN (sing-box subscription tunnel OR AmneziaWG .conf) ----

    /// <summary>Enable the VPN tunnel (routes traffic through the selected server / config).</summary>
    public bool VpnEnabled { get; set; }

    /// <summary>Which engine the VPN toggle uses: a sing-box link/subscription, or an AmneziaWG .conf.</summary>
    public VpnKind VpnKind { get; set; } = VpnKind.Singbox;

    /// <summary>true = ТУННЕЛЬ (route everything); false = ПРОКСИ (smart split, lowest ping). sing-box only.</summary>
    public bool VpnFull { get; set; }

    /// <summary>Subscription URL, or a single vless://ss://vmess://trojan:// link.</summary>
    public string SubscriptionUrl { get; set; } = "";

    /// <summary>Name of the profile selected from the subscription.</summary>
    public string? SelectedProfileName { get; set; }

    /// <summary>Raw AmneziaWG .conf text (used when VpnKind == Amnezia). Kept so it survives restarts.</summary>
    public string AmneziaConfig { get; set; } = "";

    // ---- Telegram (WebSocket bridge — Flowseal tg-ws-proxy) ----

    /// <summary>Run the bundled tg-ws-proxy on Connect so Telegram works. It's a local MTProto proxy
    /// that wraps Telegram in a WebSocket/TLS tunnel to Telegram's OWN data-centres — looks like plain
    /// HTTPS to the DPI, needs no third-party server. On by default.</summary>
    public bool TelegramFixEnabled { get; set; } = true;

    /// <summary>Stable 32-hex MTProto secret for the local proxy. Generated once and kept, so the
    /// tg:// proxy link Telegram remembers keeps matching across restarts.</summary>
    public string TgWsProxySecret { get; set; } = "";

    /// <summary>True once Telegram has been handed the tg:// proxy link, so we auto-prompt only on the
    /// very first connect and stay silent afterwards.</summary>
    public bool TelegramConfigured { get; set; }

    // ---- App ----

    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>App version this config was written by. On mismatch, scan settings are reset.</summary>
    public string AppVersion { get; set; } = "";
}
