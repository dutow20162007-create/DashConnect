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

    public static Result BuildAndSave(SingboxProfile profile, bool full, GameRouteSet routes)
    {
        var outbound = SubscriptionManager.BuildOutbound(profile, out var err);
        if (outbound is null)
            return new Result(false, err ?? "не удалось разобрать сервер", null);

        var rules = new List<object> { new Dictionary<string, object?> { ["action"] = "sniff" } };

        // Never tunnel local/LAN traffic.
        rules.Add(new Dictionary<string, object?> { ["ip_is_private"] = true, ["outbound"] = "direct" });

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
                    ["stack"] = "mixed",
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
