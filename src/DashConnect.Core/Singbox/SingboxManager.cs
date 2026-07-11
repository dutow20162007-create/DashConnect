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

            // No output redirection (CreateNoWindow keeps it windowless) — piping sing-box's chatty
            // logs to the UI dispatcher floods and freezes the window, same as winws did.
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(configPath) ?? Paths.SingboxDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(configPath);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            Log.Info("singbox", "запуск sing-box (TUN + маршрутизация)");
            if (!proc.Start()) { proc.Dispose(); return false; }

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
                Log.Error("singbox", $"sing-box сразу завершился (код {proc.ExitCode}) — проверьте конфиг/прокси");
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
        finally { proc.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
