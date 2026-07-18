using System.Text.Json;
using DashConnect.Core.Logging;
using DashConnect.Core.Util;

namespace DashConnect.Core.Singbox;

/// <summary>
/// Builds a sing-box TUN config from a selected subscription profile.
///   * Full   (ТУННЕЛЬ) — every flow goes through the proxy (Telegram, games, everything work).
///   * Smart  (ПРОКСИ)  — only games + known-blocked services go through the proxy; the rest stays
///                        direct on the ISP for the lowest possible ping.
/// Private/LAN traffic always stays direct.
/// </summary>
public static class SingboxTunnelBuilder
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Compact set of blocked/geo-restricted service suffixes used by Smart mode.
    private static readonly string[] BlockedSuffixes =
    {
        "discord.com", "discord.gg", "discord.media", "discordapp.com", "discordapp.net", "discordcdn.com",
        "telegram.org", "t.me", "telegram.me", "telegra.ph", "telesco.pe", "telegram-cdn.org",
        "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com", "youtubei.googleapis.com",
        "instagram.com", "cdninstagram.com", "facebook.com", "fbcdn.net", "meta.com", "threads.net",
        "twitter.com", "x.com", "twimg.com", "t.co",
        "twitch.tv", "ttvnw.net", "jtvnw.net",
        "openai.com", "chatgpt.com", "anthropic.com", "claude.ai", "gemini.google.com",
        "linkedin.com", "licdn.com", "signal.org", "proton.me", "protonvpn.com",
        "netflix.com", "nflxvideo.net", "spotify.com", "scdn.co", "soundcloud.com",
        // Games / launchers / services
        "steampowered.com", "steamcommunity.com", "steamstatic.com", "steamcontent.com",
        "faceit.com", "faceit-cdn.net", "epicgames.com", "epicgames.dev", "unrealengine.com",
        "fortnite.com", "riotgames.com", "riotcdn.net", "playvalorant.com", "battle.net",
        "blizzard.com", "ea.com", "origin.com", "ubisoft.com", "ubisoftconnect.com",
        "rockstargames.com", "gog.com", "xboxlive.com", "playstation.net", "roblox.com",
        "rbxcdn.com", "minecraftservices.com", "bhvr.com", "easyanticheat.net", "battleye.com",
    };

    // Telegram data-center ranges — Telegram Desktop connects to these by IP (MTProto), not by
    // domain, so smart mode must route them through the proxy explicitly.
    private static readonly string[] TelegramCidrs =
    {
        "91.108.4.0/22", "91.108.8.0/22", "91.108.12.0/22", "91.108.16.0/22", "91.108.20.0/22",
        "91.108.56.0/22", "149.154.160.0/20", "91.105.192.0/23", "185.76.151.0/24", "95.161.64.0/20",
    };

    public sealed record Result(bool Ok, string? Error, string? Json);

    /// <summary>
    /// Builds the "keep the VPN server itself out of the tunnel" route rule for an outbound.
    /// Returns null when the server address can't be determined (we then just don't add the rule).
    /// </summary>
    private static Dictionary<string, object?>? BuildServerBypassRule(Dictionary<string, object?> outbound)
    {
        if (!outbound.TryGetValue("server", out var raw) || raw is not string host || string.IsNullOrWhiteSpace(host))
            return null;

        var cidrs = new List<string>();
        void Add(System.Net.IPAddress a) => cidrs.Add(
            a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"{a}/128" : $"{a}/32");

        if (System.Net.IPAddress.TryParse(host, out var literal))
        {
            Add(literal);
        }
        else
        {
            // A domain server is dialled by its RESOLVED address, so a domain rule would never match
            // that connection — resolve here and pin every address the server answers with.
            try
            {
                var lookup = System.Net.Dns.GetHostAddressesAsync(host);
                if (lookup.Wait(TimeSpan.FromSeconds(3)))
                    foreach (var a in lookup.Result) Add(a);
                else
                    Log.Warn("singbox", $"адрес сервера {host} не разрешился за 3 с — правило обхода пропущено");
            }
            catch (Exception ex)
            {
                Log.Warn("singbox", $"не удалось разрешить адрес сервера {host}: {ex.Message}");
            }
        }

        if (cidrs.Count == 0) return null;
        Log.Info("singbox", $"сервер вне туннеля: {string.Join(", ", cidrs)}");
        return new Dictionary<string, object?> { ["ip_cidr"] = cidrs, ["outbound"] = "direct" };
    }

    public static Result BuildAndSave(SingboxProfile profile, bool full, GameRouteSet routes)
    {
        var outbound = SubscriptionManager.BuildOutbound(profile, out var err);
        if (outbound is null)
            return new Result(false, err ?? "не удалось разобрать сервер", null);

        var rules = new List<object>
        {
            new Dictionary<string, object?> { ["action"] = "sniff" },
            // Hijack the OS's DNS queries into sing-box's own DNS engine (the `dns` block below).
            // WITHOUT this, in a full tunnel DNS has no resolver, so nothing resolves and no traffic
            // flows even though the tunnel reports "up" — this was the "VLESS stopped working" bug.
            new Dictionary<string, object?> { ["protocol"] = "dns", ["action"] = "hijack-dns" },
        };

        // Never tunnel local/LAN traffic.
        rules.Add(new Dictionary<string, object?> { ["ip_is_private"] = true, ["outbound"] = "direct" });

        // Never route the proxy server's OWN address back into the tunnel. sing-box dials the VLESS
        // server, auto_route captures that dial in our own TUN, and route.final hands it straight back
        // to the same proxy outbound — the tunnel tries to reach the server through itself. sing-box
        // reports "started" and the adapter shows Up while NOTHING flows, which is exactly the
        // "VLESS подключается, но ничего не работает" report. Confirmed in a live sing-box log:
        //     inbound/tun[tun-in]:      inbound connection to <server>:443
        //     outbound/vless[proxy]:   outbound connection to <server>:443
        // With this rule the same dial goes out via outbound/direct, as it must.
        var bypass = BuildServerBypassRule(outbound);
        if (bypass is not null) rules.Add(bypass);

        string final;
        if (full)
        {
            final = SubscriptionManager.OutboundTag; // everything through the proxy
        }
        else
        {
            final = "direct"; // smart split
            var procNames = routes.AllProcessNames.ToList();
            if (procNames.Count > 0)
                rules.Add(new Dictionary<string, object?> { ["process_name"] = procNames, ["outbound"] = SubscriptionManager.OutboundTag });

            var domains = routes.AllDomainSuffixes.Concat(BlockedSuffixes).Distinct().ToList();
            rules.Add(new Dictionary<string, object?> { ["domain_suffix"] = domains, ["outbound"] = SubscriptionManager.OutboundTag });

            var cidrs = routes.AllIpCidrs.Concat(TelegramCidrs).Distinct().ToList();
            rules.Add(new Dictionary<string, object?> { ["ip_cidr"] = cidrs, ["outbound"] = SubscriptionManager.OutboundTag });
        }

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?> { ["level"] = "warn", ["timestamp"] = true },
            // DNS engine for the tunnel. remote-dns (DoH, through the proxy) resolves everything;
            // bootstrap-dns (plain UDP) resolves the DoH server's / VLESS server's own name and is the
            // domain resolver so it never deadlocks inside the tunnel. Required by sing-box 1.12+.
            // NOTE: bootstrap-dns must NOT set detour:"direct" — sing-box 1.13 FATALs at runtime with
            // "detour to an empty direct outbound makes no sense" (check passes, but run dies). Omitting
            // the detour makes the bootstrap query go direct by default, which is exactly what we want.
            ["dns"] = new Dictionary<string, object?>
            {
                ["servers"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "https", ["tag"] = "remote-dns", ["server"] = "1.1.1.1",
                        ["detour"] = SubscriptionManager.OutboundTag, ["domain_resolver"] = "bootstrap-dns",
                    },
                    new Dictionary<string, object?>
                    {
                        ["type"] = "udp", ["tag"] = "bootstrap-dns", ["server"] = "1.1.1.1",
                    },
                },
                ["final"] = "remote-dns",
                ["strategy"] = "prefer_ipv4",
            },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = "dash0",
                    ["address"] = new[] { "172.19.0.1/30" },
                    ["auto_route"] = true,
                    ["strict_route"] = true,
                    // gvisor (userspace TCP/IP), NOT "mixed"/"system": on Windows the system stack tries
                    // to listen on the TUN address itself and sing-box dies with
                    // "starting tun stack: listen tcp4 172.19.0.1:0: socket: A protocol was specified…".
                    // gvisor needs no socket on the TUN, so the tunnel actually comes up.
                    ["stack"] = "gvisor",
                    ["mtu"] = 9000,
                },
            },
            ["outbounds"] = new object[]
            {
                outbound,
                new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = "direct" },
            },
            ["route"] = new Dictionary<string, object?>
            {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "bootstrap-dns", // required by sing-box 1.12+
                ["final"] = final,
                ["rules"] = rules,
            },
        };

        var json = JsonSerializer.Serialize(config, Json);
        try
        {
            Paths.EnsureDirectories();
            File.WriteAllText(Paths.SingboxConfig, json);
            Log.Info("singbox", $"config: {(full ? "ТУННЕЛЬ (всё)" : "ПРОКСИ (умный сплит)")} через «{profile.Name}»");
        }
        catch (Exception ex)
        {
            return new Result(false, $"не удалось записать config.json: {ex.Message}", json);
        }
        return new Result(true, null, json);
    }
}
