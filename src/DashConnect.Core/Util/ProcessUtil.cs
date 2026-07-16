using System.Diagnostics;
using DashConnect.Core.Logging;

namespace DashConnect.Core.Util;

public static class ProcessUtil
{
    /// <summary>
    /// Kills every running process whose image name matches <paramref name="processName"/>
    /// (without extension). Returns the number of processes terminated.
    /// </summary>
    public static async Task<int> KillByNameAsync(string processName, CancellationToken ct = default)
    {
        int killed = 0;
        Process[] procs;
        try { procs = Process.GetProcessesByName(processName); }
        catch (Exception ex) { Log.Warn("proc", $"enumerate '{processName}' failed: {ex.Message}"); return 0; }

        foreach (var p in procs)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                await WaitForExitSafeAsync(p, TimeSpan.FromSeconds(2), ct);
                killed++;
                Log.Debug("proc", $"terminated {processName} (pid {p.Id})");
            }
            catch (Exception ex)
            {
                Log.Warn("proc", $"kill {processName} (pid {p.Id}) failed: {ex.Message}");
            }
            finally { p.Dispose(); }
        }
        return killed;
    }

    private static async Task WaitForExitSafeAsync(Process p, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await p.WaitForExitAsync(cts.Token);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Runs a short-lived console command (e.g. sc.exe) and returns (exitCode, stdout+stderr).
    /// Never throws; returns exit code -1 on launch failure.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = new Process { StartInfo = psi };
            if (!p.Start()) return (-1, "failed to start");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            var output = (await outTask) + (await errTask);
            return (p.ExitCode, output.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
