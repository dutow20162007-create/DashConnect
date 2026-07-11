namespace DashConnect.Core.Models;

/// <summary>Definition of a host to probe during connectivity testing.</summary>
public sealed class ProbeTarget
{
    public required string Label { get; init; }   // "Discord", "YouTube"
    public required string Host { get; init; }     // gateway.discord.gg
    public int Port { get; init; } = 443;
    public bool WebSocket { get; init; }           // probe wss:// handshake too
    public string? WebSocketPath { get; init; }    // e.g. "/?v=10&encoding=json"
    public bool Https { get; init; } = true;       // attempt an HTTPS request
    public bool Critical { get; init; } = true;    // must be Open for a strategy to be accepted
}

/// <summary>Outcome of probing a single host.</summary>
public sealed class HostProbeResult
{
    public required string Label { get; init; }
    public required string Host { get; init; }
    public bool Critical { get; init; } = true;
    public bool TcpConnected { get; init; }
    public bool TlsHandshakeOk { get; init; }
    public bool WebSocketOk { get; init; }
    public int? HttpStatus { get; init; }
    public double HandshakeMs { get; init; }
    public ServiceVerdict Verdict { get; init; }
    public string? Detail { get; init; }

    /// <summary>Points contributed toward a strategy score (higher = healthier).</summary>
    public double Score => Verdict switch
    {
        ServiceVerdict.Open => 100 - Math.Min(HandshakeMs, 90) / 10.0,   // 91..100
        ServiceVerdict.Throttled => 40 - Math.Min(HandshakeMs, 300) / 20.0,
        ServiceVerdict.Blocked => 0,
        ServiceVerdict.Unreachable => -5,
        _ => 0
    };
}

/// <summary>Aggregated result of probing every target once.</summary>
public sealed class DiagnosticsReport
{
    public required IReadOnlyList<HostProbeResult> Results { get; init; }
    public DateTime TimestampLocal { get; init; } = DateTime.Now;

    public int OpenCount => Results.Count(r => r.Verdict == ServiceVerdict.Open);
    public bool AllOpen => Results.Count > 0 && Results.All(r => r.Verdict == ServiceVerdict.Open);

    /// <summary>True when every critical service (Discord, YouTube, Telegram) is Open.</summary>
    public bool AllCriticalOpen
    {
        get
        {
            var crit = Results.Where(r => r.Critical).ToList();
            return crit.Count > 0 && crit.All(r => r.Verdict == ServiceVerdict.Open);
        }
    }
    public bool AnyImpaired => Results.Any(r => r.Verdict is ServiceVerdict.Blocked or ServiceVerdict.Throttled);
    public double TotalScore => Results.Sum(r => r.Score);
    public double AverageHandshakeMs =>
        Results.Where(r => r.TlsHandshakeOk).Select(r => r.HandshakeMs).DefaultIfEmpty(0).Average();
}

/// <summary>A strategy paired with the diagnostics it produced, for ranking.</summary>
public sealed class StrategyEvaluation
{
    public required ZapretStrategy Strategy { get; init; }
    public required DiagnosticsReport Report { get; init; }
    public double Score => Report.TotalScore;
}
