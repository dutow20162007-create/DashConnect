using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Diagnostics;
using DashConnect.Core.Models;

namespace DashConnect.Core.Diagnostics;

/// <summary>
/// Probes target services with fresh (un-pooled) TCP + TLS handshakes and, where relevant,
/// WebSocket upgrades. The TLS handshake is the authoritative DPI signal: a TCP connect that
/// succeeds while the ClientHello is reset or times out is the classic censorship fingerprint.
///
/// Two probe sets are exposed:
///   * DefaultTargets   — full verification (Discord + gateway WS, YouTube + CDN)
///   * SelectionTargets — fast TLS-only critical probes used while trialing Zapret strategies
/// </summary>
public sealed class ConnectivityTester
{
    // Tightened for a snappy connect flow.
    private const int TcpTimeoutMs = 1200;
    private const int TlsTimeoutMs = 1800;
    private const int WsTimeoutMs = 2500;
    private const double ThrottleMs = 1200; // TLS ok but slower than this ⇒ throttled

    /// <summary>Full verification set shown in the dashboard.</summary>
    public static IReadOnlyList<ProbeTarget> DefaultTargets { get; } = new List<ProbeTarget>
    {
        new() { Label = "Discord",         Host = "discord.com",                Https = true,  Critical = true },
        new() { Label = "Discord (шлюз)",  Host = "gateway.discord.gg",         WebSocket = true,
                WebSocketPath = "/?v=10&encoding=json", Https = false, Critical = true },
        new() { Label = "YouTube",         Host = "www.youtube.com",            Https = true,  Critical = true },
        new() { Label = "YouTube CDN",     Host = "redirector.googlevideo.com", Https = true,  Critical = false },
        // Telegram servers over TLS (SNI) — reachable through the DPI bypass, so this shows a real ping
        // instead of the "no connection" you get probing a raw MTProto DC IP (which the ISP drops).
        new() { Label = "Telegram",        Host = "web.telegram.org", Https = true, Critical = false },
        // The local WebSocket bridge: green with a ~0 ms ping when the Telegram fix is actually running.
        new() { Label = "Telegram (мост)", Host = "127.0.0.1", Port = TgBridgePort, TcpOnly = true, Https = false, Critical = false },
    };

    /// <summary>Port the bundled tg-ws-proxy bridge listens on (kept in sync with TgWsProxyManager.Port).</summary>
    public const int TgBridgePort = 1443;

    /// <summary>Fast critical-only set (TLS handshake only) used to score strategies quickly.</summary>
    public static IReadOnlyList<ProbeTarget> SelectionTargets { get; } = new List<ProbeTarget>
    {
        new() { Label = "Discord",        Host = "discord.com",        Https = false, Critical = true },
        new() { Label = "YouTube",        Host = "www.youtube.com",    Https = false, Critical = true },
        new() { Label = "Discord (шлюз)", Host = "gateway.discord.gg", Https = false, Critical = true },
    };

    public async Task<DiagnosticsReport> RunAsync(
        IReadOnlyList<ProbeTarget> targets, CancellationToken ct = default, bool retryOnFail = false)
    {
        var tasks = targets.Select(t => ProbeGuardedAsync(t, ct, retryOnFail)).ToArray();
        var results = await Task.WhenAll(tasks);
        return new DiagnosticsReport { Results = results };
    }

    /// <summary>Probe a target; optionally retry once on failure to avoid a false "no connection".</summary>
    private async Task<HostProbeResult> ProbeGuardedAsync(ProbeTarget target, CancellationToken ct, bool retryOnFail)
    {
        var result = await ProbeOnceAsync(target, ct);
        // A single fresh probe can transiently fail (winws mid-reset, a slow first SYN, one dropped
        // packet) and mis-report "no connection". For the user-facing availability report, retry once
        // and keep the better outcome so a working service never shows as unreachable.
        if (retryOnFail && result.Verdict is ServiceVerdict.Unreachable or ServiceVerdict.Blocked)
        {
            try { await Task.Delay(300, ct); } catch { }
            var retry = await ProbeOnceAsync(target, ct);
            if (retry.Verdict is ServiceVerdict.Open or ServiceVerdict.Throttled)
                return retry;
        }
        return result;
    }

    /// <summary>Hard overall cap per target so one stuck probe can never hang the whole report.</summary>
    private async Task<HostProbeResult> ProbeOnceAsync(ProbeTarget target, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await ProbeAsync(target, cts.Token);
        }
        catch (Exception ex)
        {
            return new HostProbeResult
            {
                Label = target.Label, Host = target.Host, Critical = target.Critical,
                Verdict = ServiceVerdict.Blocked, Detail = ex is OperationCanceledException ? "timeout" : ex.Message,
            };
        }
    }

    public Task<DiagnosticsReport> RunDefaultAsync(CancellationToken ct = default)
        => RunAsync(DefaultTargets, ct, retryOnFail: true);

    /// <summary>Fast, critical-only probe used during strategy selection.</summary>
    public Task<DiagnosticsReport> RunSelectionAsync(CancellationToken ct = default)
        => RunAsync(SelectionTargets, ct);

    public async Task<HostProbeResult> ProbeAsync(ProbeTarget target, CancellationToken ct)
    {
        bool tcp = false, tls = false, ws = false;
        int? httpStatus = null;
        double handshakeMs = 0;
        string? detail = null;

        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TcpTimeoutMs);

            var tcpSw = Stopwatch.StartNew();
            try
            {
                await client.ConnectAsync(target.Host, target.Port, connectCts.Token);
                tcp = client.Connected;
            }
            catch (Exception ex)
            {
                detail = $"tcp: {Simplify(ex)}";
                return Build(target, tcp, tls, ws, httpStatus, handshakeMs, detail);
            }

            if (target.TcpOnly)
            {
                // Raw DC (MTProto isn't TLS): a successful TCP connect means the app's Telegram can reach it.
                var connMs = tcpSw.Elapsed.TotalMilliseconds;
                return new HostProbeResult
                {
                    Label = target.Label, Host = target.Host, Critical = target.Critical,
                    TcpConnected = true, HandshakeMs = connMs,
                    Verdict = connMs > ThrottleMs ? ServiceVerdict.Throttled : ServiceVerdict.Open,
                };
            }

            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, AcceptAnyCert);
            var sw = Stopwatch.StartNew();
            try
            {
                using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tlsCts.CancelAfter(TlsTimeoutMs);
                var opts = new SslClientAuthenticationOptions
                {
                    TargetHost = target.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                };
                await ssl.AuthenticateAsClientAsync(opts, tlsCts.Token);
                handshakeMs = sw.Elapsed.TotalMilliseconds;
                tls = true;
            }
            catch (Exception ex)
            {
                handshakeMs = sw.Elapsed.TotalMilliseconds;
                detail = $"tls: {Simplify(ex)}";
                return Build(target, tcp, tls, ws, httpStatus, handshakeMs, detail);
            }

            if (target.Https)
            {
                try { httpStatus = await MinimalHttpGetAsync(ssl, target.Host, ct); }
                catch (Exception ex) { detail = $"http: {Simplify(ex)}"; }
            }
        }
        catch (Exception ex)
        {
            detail = Simplify(ex);
        }

        if (target.WebSocket)
        {
            try { ws = await ProbeWebSocketAsync(target, ct); }
            catch (Exception ex) { detail = $"ws: {Simplify(ex)}"; }
        }

        return Build(target, tcp, tls, ws, httpStatus, handshakeMs, detail);
    }

    private static HostProbeResult Build(
        ProbeTarget t, bool tcp, bool tls, bool ws, int? http, double ms, string? detail)
    {
        ServiceVerdict verdict;
        if (!tcp) verdict = ServiceVerdict.Unreachable;
        else if (!tls) verdict = ServiceVerdict.Blocked;
        else if (t.WebSocket && !ws) verdict = ServiceVerdict.Throttled;
        else if (ms > ThrottleMs) verdict = ServiceVerdict.Throttled;
        else verdict = ServiceVerdict.Open;

        return new HostProbeResult
        {
            Label = t.Label,
            Host = t.Host,
            Critical = t.Critical,
            TcpConnected = tcp,
            TlsHandshakeOk = tls,
            WebSocketOk = ws,
            HttpStatus = http,
            HandshakeMs = ms,
            Verdict = verdict,
            Detail = detail,
        };
    }

    private static async Task<int?> MinimalHttpGetAsync(SslStream ssl, string host, CancellationToken ct)
    {
        var req = $"GET / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: DashConnect/1.0\r\nAccept: */*\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(req);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TlsTimeoutMs);
        await ssl.WriteAsync(bytes, cts.Token);
        await ssl.FlushAsync(cts.Token);

        var buf = new byte[256];
        int read = await ssl.ReadAsync(buf, cts.Token);
        if (read <= 0) return null;
        var statusLine = Encoding.ASCII.GetString(buf, 0, read);
        var parts = statusLine.Split(' ', 3);
        if (parts.Length >= 2 && int.TryParse(parts[1], out var code)) return code;
        return null;
    }

    private static async Task<bool> ProbeWebSocketAsync(ProbeTarget target, CancellationToken ct)
    {
        var uri = new Uri($"wss://{target.Host}{target.WebSocketPath ?? "/"}");
        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = AcceptAnyCert;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(WsTimeoutMs);
        await socket.ConnectAsync(uri, cts.Token);
        bool ok = socket.State == WebSocketState.Open;
        socket.Abort(); // instant, no network round-trip — CloseAsync could hang forever if black-holed
        return ok;
    }

    private static bool AcceptAnyCert(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
        => true;

    private static string Simplify(Exception ex)
    {
        var e = ex is AggregateException agg ? agg.GetBaseException() : ex;
        if (e is OperationCanceledException) return "timeout";
        return e.Message.Length > 80 ? e.Message[..80] : e.Message;
    }
}
