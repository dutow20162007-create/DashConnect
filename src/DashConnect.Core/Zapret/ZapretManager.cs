using System.Diagnostics;
using DashConnect.Core.Logging;
using DashConnect.Core.Models;
using DashConnect.Core.Util;

namespace DashConnect.Core.Zapret;

/// <summary>
/// Owns the lifecycle of the <c>winws.exe</c> DPI-bypass process and the WinDivert driver it loads.
/// Guarantees clean start/stop and reaps orphaned instances so the WinDivert handle is never leaked.
/// </summary>
public sealed class ZapretManager : IAsyncDisposable
{
    private const string WinwsProcess = "winws";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public ZapretStrategy? Current { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Terminates any stray winws.exe and best-effort stops the WinDivert service.</summary>
    public static async Task KillOrphansAsync(CancellationToken ct = default)
    {
        int n = await ProcessUtil.KillByNameAsync(WinwsProcess, ct);
        if (n > 0) Log.Info("zapret", $"убрано зависших winws: {n}");
        // Fully unload the WinDivert kernel driver so the NEXT winws starts from a clean state.
        // winws recreates the driver service on launch. Without this reset, cycling winws during a
        // preset sweep can leave the driver in a half-open state that wedges the whole network stack
        // on some machines (the "freeze on the 2nd preset"). Safe here because winws is already dead.
        await ProcessUtil.RunAsync("sc.exe", "stop WinDivert", TimeSpan.FromSeconds(3), ct);
        await Task.Delay(350, ct);
    }

    /// <summary>
    /// Starts winws.exe with the given strategy. Returns false if the binary is missing or the
    /// process exits immediately (typically another instance still holds WinDivert, or bad args).
    /// </summary>
    public async Task<bool> StartAsync(string zapretRoot, ZapretStrategy strategy, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var exe = StrategyProvider.WinwsPath(zapretRoot);
            if (!File.Exists(exe))
            {
                Log.Error("zapret", $"winws.exe not found at {exe}");
                return false;
            }

            await StopInternalAsync(ct);
            await KillOrphansAsync(ct);

            // No output redirection: CreateNoWindow keeps winws windowless and the OS discards its
            // (very chatty) console output. Redirecting + forwarding it to the UI log was what
            // flooded the dispatcher and froze the window.
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = StrategyProvider.BinDir(zapretRoot),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in strategy.Arguments) psi.ArgumentList.Add(arg);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            Log.Info("zapret", $"запуск winws — «{strategy.Name}» ({strategy.Arguments.Count} арг.)");
            if (!proc.Start())
            {
                Log.Error("zapret", "winws не запустился");
                proc.Dispose();
                return false;
            }

            // Detect an immediate crash (bad args / WinDivert still busy).
            try
            {
                await Task.Delay(500, ct);
            }
            catch
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                proc.Dispose();
                throw;
            }
            if (proc.HasExited)
            {
                Log.Error("zapret", $"winws сразу завершился (код {proc.ExitCode}) — «{strategy.Name}»");
                proc.Dispose();
                return false;
            }

            _process = proc;
            Current = strategy;
            Log.Info("zapret", $"winws running (pid {proc.Id}) — DPI bypass active");
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
        Current = null;
        if (proc is null) return;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                await proc.WaitForExitAsync(cts.Token);
            }
            Log.Info("zapret", "winws stopped");
        }
        catch (Exception ex)
        {
            Log.Warn("zapret", $"stop winws: {ex.Message}");
        }
        finally
        {
            proc.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
