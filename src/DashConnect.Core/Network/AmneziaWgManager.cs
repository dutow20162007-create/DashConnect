using System.Diagnostics;
using System.Text;
using DashConnect.Core.Logging;
using DashConnect.Core.Util;

namespace DashConnect.Core.Network;

/// <summary>Summary of a pasted AmneziaWG .conf (for validation + display).</summary>
public sealed record AmneziaConfigInfo(bool Valid, string? Endpoint, bool Obfuscated, string? Error);

/// <summary>
/// Runs an AmneziaWG (obfuscated WireGuard) tunnel from a user-supplied .conf using the bundled,
/// signed <c>amneziawg.exe</c> (the official Amnezia Windows client, MIT). It installs a headless
/// Windows tunnel service — <c>amneziawg.exe /installtunnelservice &lt;conf&gt;</c> — which reads the
/// .conf directly, including the Jc/S1-S4/H1-H4/I1-I3 obfuscation params that plain WireGuard and
/// sing-box can't speak. Always a full tunnel (per the config's AllowedIPs). Requires admin (we run
/// elevated) and wintun.dll (bundled next to the exe).
/// </summary>
public sealed class AmneziaWgManager : IAsyncDisposable
{
    private const string TunnelName = "dashconnect"; // == .conf base name == service suffix
    // The fork may register under either prefix depending on build; handle both.
    private static readonly string[] ServiceNames =
        { $"AmneziaWGTunnel${TunnelName}", $"WireGuardTunnel${TunnelName}" };

    private string? _exePath;

    public static string ExePath(string zapretRoot) => Path.Combine(zapretRoot, "amnezia", "amneziawg.exe");

    public bool IsRunning => FindRunningService() is not null;

    /// <summary>Validates a pasted .conf and pulls out the endpoint + whether it's obfuscated AmneziaWG.</summary>
    public static AmneziaConfigInfo Parse(string configText)
    {
        if (string.IsNullOrWhiteSpace(configText))
            return new(false, null, false, "пусто");

        bool hasInterface = false, hasPeer = false, hasKey = false, obfuscated = false;
        string? endpoint = null;

        foreach (var raw in configText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase)) { hasInterface = true; continue; }
            if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase)) { hasPeer = true; continue; }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();

            if (key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase)) hasKey = true;
            else if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase)) endpoint = val;
            else if (key is "Jc" or "Jmin" or "Jmax" or "S1" or "S2" or "H1" or "H2" or "I1")
                obfuscated = true;
        }

        if (!hasInterface || !hasPeer) return new(false, endpoint, obfuscated, "нет секции [Interface]/[Peer]");
        if (!hasKey) return new(false, endpoint, obfuscated, "нет PrivateKey");
        if (string.IsNullOrWhiteSpace(endpoint)) return new(false, endpoint, obfuscated, "нет Endpoint");
        return new(true, endpoint, obfuscated, null);
    }

    /// <summary>Writes the .conf and brings up the tunnel service. Returns true once it's RUNNING.</summary>
    public async Task<bool> StartAsync(string zapretRoot, string configText, Action<string>? status = null, CancellationToken ct = default)
    {
        var exe = ExePath(zapretRoot);
        if (!File.Exists(exe)) { Log.Warn("awg", "amneziawg.exe не найден в папке amnezia"); return false; }
        _exePath = exe;

        var info = Parse(configText);
        if (!info.Valid) { status?.Invoke($"Конфиг Amnezia неверный: {info.Error}"); return false; }

        await StopAsync(CancellationToken.None); // clear any prior tunnel first — must fully complete even if the connect is cancelled

        Directory.CreateDirectory(Paths.AmneziaDir);
        await File.WriteAllTextAsync(Paths.AmneziaConfigFile, NormalizeNewlines(configText), new UTF8Encoding(false), ct);

        status?.Invoke("Поднимаю AmneziaWG-туннель…");
        var install = await RunAsync(exe, $"/installtunnelservice \"{Paths.AmneziaConfigFile}\"", Path.GetDirectoryName(exe)!, ct);
        if (install.exit != 0)
        {
            Log.Warn("awg", $"installtunnelservice exit {install.exit}: {install.output}");
            status?.Invoke("AmneziaWG не запустился (см. журнал)");
            return false;
        }

        for (int i = 0; i < 16 && !ct.IsCancellationRequested; i++)
        {
            if (IsRunning) { status?.Invoke($"AmneziaWG активен ({info.Endpoint})"); Log.Info("awg", "tunnel running"); return true; }
            await Task.Delay(500, ct);
        }
        Log.Warn("awg", "туннель не перешёл в RUNNING");
        return IsRunning;
    }

    /// <summary>Stops + removes the tunnel service (idempotent — safe if nothing is installed).</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        var exe = _exePath ?? "";
        if (File.Exists(exe))
            await RunAsync(exe, $"/uninstalltunnelservice {TunnelName}", Path.GetDirectoryName(exe)!, ct);

        // Belt-and-suspenders: force the service gone even if the exe path is unknown/failed.
        foreach (var svc in ServiceNames)
        {
            if (ServiceExists(svc))
            {
                await RunAsync("sc.exe", $"stop \"{svc}\"", null, ct);
                await RunAsync("sc.exe", $"delete \"{svc}\"", null, ct);
            }
        }
    }

    /// <summary>Removes any leftover DashConnect AmneziaWG tunnel (crash recovery on startup/exit).</summary>
    public static async Task KillOrphansAsync()
    {
        foreach (var svc in ServiceNames)
        {
            if (ServiceExists(svc))
            {
                await RunAsync("sc.exe", $"stop \"{svc}\"", null, CancellationToken.None);
                await RunAsync("sc.exe", $"delete \"{svc}\"", null, CancellationToken.None);
            }
        }
    }

    private static string? FindRunningService()
        => ServiceNames.FirstOrDefault(s => ServiceState(s) == "RUNNING");

    private static bool ServiceExists(string name) => ServiceState(name) is not null;

    /// <summary>Returns the service STATE word (RUNNING/STOPPED/…) or null if the service doesn't exist.</summary>
    private static string? ServiceState(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"query \"{name}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            if (outp.Contains("does not exist", StringComparison.OrdinalIgnoreCase)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(outp, @"STATE\s+:\s+\d+\s+(\w+)");
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }
        catch { return null; }
    }

    private static async Task<(int exit, string output)> RunAsync(string exe, string args, string? workingDir, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (workingDir is not null) psi.WorkingDirectory = workingDir;
            using var p = Process.Start(psi);
            if (p is null) return (-1, "process start failed");
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, ((await outTask) + (await errTask)).Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    private static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n").Trim() + "\r\n";

    public async ValueTask DisposeAsync() => await StopAsync();
}
