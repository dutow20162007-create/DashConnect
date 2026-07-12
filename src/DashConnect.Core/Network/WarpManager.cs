using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using DashConnect.Core.Logging;

namespace DashConnect.Core.Network;

/// <summary>
/// Runs a bundled, open-source Cloudflare WARP client (warp-plus, bepass-org) as a local SOCKS5
/// relay so Telegram can reach its data-centres through Cloudflare's network — no proxy/VPN of the
/// user's own. Tries direct WARP (endpoint scan) first, then falls back to the Psiphon chain
/// (--cfon) when the ISP blocks Cloudflare outright. Success is verified by an actual SOCKS5 CONNECT
/// to a real Telegram DC, not merely "the process started".
/// </summary>
public sealed class WarpManager : IAsyncDisposable
{
    // warp-plus: single Go binary, anonymous WARP (no Cloudflare login), SOCKS5, censorship-resilient.
    private const string ZipUrl =
        "https://github.com/bepass-org/warp-plus/releases/download/v1.2.6/warp-plus_windows-amd64.zip";

    public const int SocksPort = 41080;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DashConnect", "warp");
    private static string Exe => Path.Combine(Dir, "warp-plus.exe");

    private Process? _proc;

    public bool IsRunning => _proc is { HasExited: false };
    public static int Port => SocksPort;

    /// <summary>Downloads + extracts warp-plus.exe (idempotent). Returns its path or null.</summary>
    public async Task<string?> EnsureAsync(Action<string>? status = null, CancellationToken ct = default)
    {
        if (File.Exists(Exe)) return Exe;
        try
        {
            Directory.CreateDirectory(Dir);
            status?.Invoke("Скачиваю WARP…");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var zip = Path.Combine(Dir, "warp.zip");
            await File.WriteAllBytesAsync(zip, await http.GetByteArrayAsync(ZipUrl, ct), ct);
            using (var z = ZipFile.OpenRead(zip))
            {
                var entry = z.Entries.FirstOrDefault(e => e.Name.Equals("warp-plus.exe", StringComparison.OrdinalIgnoreCase))
                            ?? z.Entries.FirstOrDefault(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null) { Log.Warn("warp", "no exe in archive"); return null; }
                entry.ExtractToFile(Exe, overwrite: true);
            }
            try { File.Delete(zip); } catch { }
            return File.Exists(Exe) ? Exe : null;
        }
        catch (Exception ex) { Log.Warn("warp", $"загрузка WARP не удалась: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Starts WARP and returns true once a Telegram DC is actually reachable through the local SOCKS.
    /// Cascade: direct WARP (endpoint scan) → Psiphon fallback.
    /// </summary>
    public async Task<bool> StartAsync(Action<string>? status = null, CancellationToken ct = default)
    {
        var exe = await EnsureAsync(status, ct);
        if (exe is null) return false;

        // Cascade, cheapest/fastest first. Each binds the SAME local SOCKS port so callers don't care
        // which one won. gool = warp-in-warp (survives light throttling); cfon = Psiphon (survives a
        // full Cloudflare block, slower). First mode whose tunnel actually reaches a Telegram DC wins.
        (string label, string args)[] modes =
        {
            ("Поднимаю WARP…",         $"--bind 127.0.0.1:{SocksPort}"),
            ("WARP (warp-in-warp)…",   $"--bind 127.0.0.1:{SocksPort} --gool"),
            ("WARP (поиск точки)…",    $"--bind 127.0.0.1:{SocksPort} --scan"),
            ("WARP через Psiphon…",    $"--bind 127.0.0.1:{SocksPort} --cfon --country AT"),
        };

        foreach (var (label, args) in modes)
        {
            if (ct.IsCancellationRequested) break;
            status?.Invoke(label);
            Stop();
            if (!Launch(exe, args)) continue;

            for (int i = 0; i < 22 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(1000, ct);
                if (!IsRunning) break;
                if (await ProbeTelegramThroughSocksAsync(ct))
                {
                    status?.Invoke("WARP активен — Telegram доступен через Cloudflare");
                    Log.Info("warp", $"telegram reachable via WARP ({args})");
                    return true;
                }
            }
        }
        Stop();
        return false;
    }

    private bool Launch(string exe, string args)
    {
        try
        {
            _proc = Process.Start(new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Dir,
            });
            return _proc is not null;
        }
        catch (Exception ex) { Log.Warn("warp", $"launch: {ex.Message}"); return false; }
    }

    public void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        _proc = null;
    }

    public static async Task KillOrphansAsync()
    {
        foreach (var p in Process.GetProcessesByName("warp-plus"))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        await Task.CompletedTask;
    }

    /// <summary>SOCKS5 CONNECT to a real Telegram DC through the local WARP proxy — the end-goal test.</summary>
    private static async Task<bool> ProbeTelegramThroughSocksAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, SocksPort, cts.Token);
            var s = c.GetStream();
            await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cts.Token);        // greet, no-auth
            var r = new byte[2];
            await s.ReadExactlyAsync(r, cts.Token);
            if (r[1] != 0x00) return false;
            // CONNECT 149.154.167.51:443 (Telegram DC2)
            await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00, 0x01, 149, 154, 167, 51, 0x01, 0xBB }, cts.Token);
            var rep = new byte[10];
            await s.ReadExactlyAsync(rep, cts.Token);
            return rep[1] == 0x00;                                                 // 0x00 = succeeded
        }
        catch { return false; }
    }

    public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
}
