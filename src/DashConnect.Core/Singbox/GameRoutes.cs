using System.Text.Json;
using System.Text.Json.Serialization;
using DashConnect.Core.Logging;
using DashConnect.Core.Util;

namespace DashConnect.Core.Singbox;

/// <summary>Routing signature for one game: which processes and domains flow through the proxy.</summary>
public sealed class GameRoute
{
    public required string Name { get; set; }
    public List<string> ProcessNames { get; set; } = new();
    public List<string> DomainSuffixes { get; set; } = new();
    public List<string> IpCidrs { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

public sealed class GameRouteSet
{
    public List<GameRoute> Games { get; set; } = new();

    public IEnumerable<string> AllProcessNames => Games.Where(g => g.Enabled).SelectMany(g => g.ProcessNames).Distinct();
    public IEnumerable<string> AllDomainSuffixes => Games.Where(g => g.Enabled).SelectMany(g => g.DomainSuffixes).Distinct();
    public IEnumerable<string> AllIpCidrs => Games.Where(g => g.Enabled).SelectMany(g => g.IpCidrs).Distinct();
}

/// <summary>
/// Default game-routing signatures plus load/save of the user-editable game-routes.json.
/// process_name matching is the primary, DNS-independent signal — every packet from the game's
/// executable is routed through the proxy regardless of how the destination IP was resolved.
/// </summary>
public static class GameRoutesStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static GameRouteSet Defaults() => new()
    {
        Games =
        {
            new GameRoute
            {
                Name = "Fortnite",
                ProcessNames =
                {
                    "FortniteClient-Win64-Shipping.exe",
                    "FortniteLauncher.exe",
                    "EpicGamesLauncher.exe",
                    "EpicWebHelper.exe",
                },
                DomainSuffixes =
                {
                    "epicgames.com", "epicgames.dev", "unrealengine.com",
                    "fortnite.com", "ol.epicgames.com", "live.on.epicgames.com",
                },
            },
            new GameRoute
            {
                Name = "Dead by Daylight",
                ProcessNames =
                {
                    "DeadByDaylight-Win64-Shipping.exe",
                    "DeadByDaylight.exe",
                },
                DomainSuffixes =
                {
                    "bhvr.com", "deadbydaylight.com",
                },
            },
            new GameRoute
            {
                Name = "FACEIT",
                ProcessNames =
                {
                    "faceitclient.exe", "faceit.exe", "faceitapp.exe", "cs2.exe",
                },
                DomainSuffixes =
                {
                    "faceit.com", "faceit-cdn.net", "faceitanalyser.com",
                },
            },
        }
    };

    public static GameRouteSet Load()
    {
        try
        {
            if (File.Exists(Paths.GameRoutesFile))
            {
                var set = JsonSerializer.Deserialize<GameRouteSet>(File.ReadAllText(Paths.GameRoutesFile), Options);
                if (set is { Games.Count: > 0 }) return set;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("routes", $"load failed, using defaults: {ex.Message}");
        }

        var defaults = Defaults();
        Save(defaults); // materialize a template the user can edit
        return defaults;
    }

    public static void Save(GameRouteSet set)
    {
        try
        {
            Paths.EnsureDirectories();
            File.WriteAllText(Paths.GameRoutesFile, JsonSerializer.Serialize(set, Options));
        }
        catch (Exception ex)
        {
            Log.Warn("routes", $"save failed: {ex.Message}");
        }
    }
}
