using System.Diagnostics;
using DashConnect.Core.Logging;
using DashConnect.Core.Util;

namespace DashConnect.Core.Singbox;

/// <summary>
/// Owns the lifecycle of the <c>sing-box.exe</c> process that runs the TUN + game-routing tunnel.
/// Reaps orphaned instances so a stale TUN adapter never blocks a fresh start.
/// </summary>
public sealed class SingboxManager : IAsyncDisposable
{
    private const string ProcName = "sing-box";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Detects ANOTHER VPN client's tunnel adapter that is currently up (v2RayTun, Nekoray, Hiddify,
    /// Clash, Outline…). Two TUN clients fight for the same packets — whoever installs its capture last
    /// wins, so ours reports "подключено" while the other one keeps carrying the traffic and the user
    /// sees a tunnel that does nothing. Returns the offending adapter name, or null when we're clear.
    /// </summary>
    public static string? DetectForeignTunnel()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;

                var name = ni.Name ?? "";
                var desc = ni.Description ?? "";
                // Skip our own adapters (sing-box "dash0", the AmneziaWG "dashconnect" tunnel).
                if (name.Contains("dash", StringComparison.OrdinalIgnoreCase)) continue;

                bool foreign =
                    desc.Contains("sing-tun", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("v2ray", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("nekoray", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("nekobox", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("hiddify", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("clash", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("outline", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("xray", StringComparison.OrdinalIgnoreCase);

                if (foreign) return name;
            }
        }
        catch (Exception ex) { Log.Debug("singbox", $"проверка чужих туннелей: {ex.Message}"); }
        return null;
    }

    public static async Task KillOrphansAsync(CancellationToken ct = default)
    {
        int n = await ProcessUtil.KillByNameAsync(ProcName, ct);
        if (n > 0) Log.Info("singbox", $"cleaned up {n} orphaned sing-box instance(s)");
        await Task.Delay(300, ct);
    }

    /// <summary>Launches sing-box with the given config. Returns false on immediate failure.</summary>
    public async Task<bool> StartAsync(string exePath, string configPath, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(exePath)) { Log.Error("singbox", $"binary missing: {exePath}"); return false; }
            if (!File.Exists(configPath)) { Log.Error("singbox", $"config missing: {configPath}"); return false; }

            await StopInternalAsync(ct);
            await KillOrphansAsync(ct);

            // Validate the config against THIS binary first. sing-box changes its config schema between
            // versions; a mismatch otherwise looks like a silent immediate exit. `check` returns the
            // exact offending field so the failure is diagnosable instead of invisible.
            var (checkCode, checkOut) = await ProcessUtil.RunAsync(
                exePath, $"check -c \"{configPath}\"", TimeSpan.FromSeconds(12), ct);
            if (checkCode != 0)
            {
                Log.Error("singbox", $"config отклонён sing-box: {checkOut}");
                return false;
            }

            // Stream sing-box's own logs to a FILE (never the UI dispatcher — that floods and freezes
            // the window). Redirecting both pipes also prevents a full pipe buffer from blocking it.
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(configPath) ?? Paths.SingboxDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(configPath);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            Log.Info("singbox", "запуск sing-box (TUN + маршрутизация)");
            if (!proc.Start()) { proc.Dispose(); return false; }
            DrainToFile(proc, Path.Combine(Paths.LogsDir, "singbox.log"));

            try
            {
                await Task.Delay(900, ct); // settle; detect an immediate crash
            }
            catch
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                proc.Dispose();
                throw;
            }

            if (proc.HasExited)
            {
                Log.Error("singbox", $"sing-box сразу завершился (код {proc.ExitCode}) — см. logs\\singbox.log");
                proc.Dispose();
                return false;
            }

            _process = proc;
            Log.Info("singbox", $"sing-box работает (pid {proc.Id})");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await StopInternalAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task StopInternalAsync(CancellationToken ct)
    {
        var proc = _process;
        _process = null;
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                await proc.WaitForExitAsync(cts.Token);
            }
            Log.Info("singbox", "sing-box stopped");
        }
        catch (Exception ex) { Log.Warn("singbox", $"stop: {ex.Message}"); }
        finally { proc.Dispose(); DisposeLogWriter(); }
    }

    private StreamWriter? _logWriter;

    /// <summary>
    /// Closes the log writer deterministically. Relying only on Process.Exited leaked the handle,
    /// because StopInternalAsync disposes the Process (killing the event) — the NEXT start then failed
    /// to open singbox.log, silently skipped pipe draining, and sing-box blocked on a full stdout pipe.
    /// That is why the tunnel worked on the first connect and died after a reconnect.
    /// </summary>
    private void DisposeLogWriter()
    {
        var w = _logWriter;
        _logWriter = null;
        if (w is not null) { try { w.Dispose(); } catch { } }
    }

    /// <summary>Streams a started process's stdout/stderr to a log file (drains both pipes so they
    /// can't fill and block sing-box).</summary>
    private void DrainToFile(Process proc, string logFile)
    {
        DisposeLogWriter(); // never carry a previous session's open handle into this start
        StreamWriter sw;
        try { sw = new StreamWriter(logFile, append: false) { AutoFlush = true }; }
        catch { return; }
        _logWriter = sw;
        var gate = new object();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (gate) { try { sw.WriteLine(e.Data); } catch { } } } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (gate) { try { sw.WriteLine("! " + e.Data); } catch { } } } };
        proc.Exited += (_, _) => DisposeLogWriter();
        try { proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
