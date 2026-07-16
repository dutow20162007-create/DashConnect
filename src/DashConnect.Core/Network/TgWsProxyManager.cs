using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DashConnect.Core.Logging;

namespace DashConnect.Core.Network;

/// <summary>
/// Runs Flowseal's open-source tg-ws-proxy (bundled headless exe) — a local MTProto proxy that wraps
/// Telegram's traffic in a WebSocket/TLS tunnel to Telegram's OWN data-centres (looks like ordinary
/// HTTPS to DPI), so Telegram works with NO third-party server. Telegram Desktop connects to it via
/// tg://proxy?server=127.0.0.1&amp;port=1443&amp;secret=dd&lt;secret&gt;. The secret is persisted by the
/// caller so the same tg:// link keeps working across restarts (Telegram remembers it).
/// </summary>
public sealed class TgWsProxyManager : IAsyncDisposable
{
    public const int Port = 1443;

    private Process? _proc;
    private string _secret = "";

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>The tg:// link Telegram Desktop opens to enable this proxy (one click to confirm).</summary>
    public string TgLink => $"tg://proxy?server=127.0.0.1&port={Port}&secret=dd{_secret}";

    /// <summary>Path to the bundled proxy inside the Zapret distribution.</summary>
    public static string ExePath(string zapretRoot) => Path.Combine(zapretRoot, "tg-ws-proxy.exe");

    /// <summary>Generate a fresh 16-byte (32-hex) MTProto secret.</summary>
    public static string NewSecret()
    {
        var b = new byte[16];
        RandomNumberGenerator.Fill(b);
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    /// <summary>
    /// Starts the proxy with the given (persisted) secret and returns true once it is listening.
    /// </summary>
    public async Task<bool> StartAsync(string zapretRoot, string secret, Action<string>? status = null, CancellationToken ct = default)
    {
        var exe = ExePath(zapretRoot);
        if (!File.Exists(exe)) { Log.Warn("tgws", "tg-ws-proxy.exe не найден в папке Zapret"); return false; }

        if (IsRunning && _secret == secret && await PortOpenAsync(ct)) return true; // already up

        // Kill our tracked process AND any orphans left by a previous app run — a leftover instance
        // holding 127.0.0.1:1443 makes the new one fail to bind ([Errno 10048]) and exit immediately,
        // which looked like "the bridge won't start". Then wait for the OS to actually free the port.
        Stop();
        await KillOrphansAsync();
        await WaitPortFreeAsync(ct);

        _secret = secret;
        status?.Invoke("Поднимаю WebSocket-мост Telegram…");
        try
        {
            _proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe, $"--port {Port} --secret {secret}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = zapretRoot,
                },
                EnableRaisingEvents = true,
            };
            // The proxy logs continuously (per-connection + periodic stats). If we redirect the pipes
            // but never read them, the OS pipe buffer fills and the proxy BLOCKS on its next write.
            // Drain both streams so it keeps running for the whole session.
            _proc.OutputDataReceived += (_, _) => { };
            _proc.ErrorDataReceived += (_, _) => { };
            if (!_proc.Start()) { Log.Warn("tgws", "процесс не стартовал"); _proc = null; return false; }
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }
        catch (Exception ex) { Log.Warn("tgws", $"launch: {ex.Message}"); _proc = null; return false; }

        for (int i = 0; i < 15 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(600, ct);
            if (!IsRunning) { Log.Warn("tgws", "процесс завершился при старте"); return false; }
            if (await PortOpenAsync(ct))
            {
                await Task.Delay(700, ct); // let the WS pool warm up a touch
                status?.Invoke("Telegram WebSocket-мост готов");
                Log.Info("tgws", $"listening on 127.0.0.1:{Port}");
                return true;
            }
        }
        Stop();
        return false;
    }

    /// <summary>Wait (up to ~3s) for port 1443 to be released after killing a previous instance.</summary>
    private static async Task WaitPortFreeAsync(CancellationToken ct)
    {
        for (int i = 0; i < 15 && !ct.IsCancellationRequested; i++)
        {
            if (!await PortOpenAsync(ct)) return;
            await Task.Delay(200, ct);
        }
    }

    private static async Task<bool> PortOpenAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, Port, cts.Token);
            return c.Connected;
        }
        catch { return false; }
    }

    public void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        _proc = null;
    }

    public static async Task KillOrphansAsync()
    {
        foreach (var p in Process.GetProcessesByName("tg-ws-proxy"))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
}
