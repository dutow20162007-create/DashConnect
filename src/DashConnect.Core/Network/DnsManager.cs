using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using DashConnect.Core.Logging;

namespace DashConnect.Core.Network;

/// <summary>
/// Switches the system to encrypted DNS (Cloudflare DoH) while connected, and restores the user's
/// ORIGINAL DNS on disconnect. This defeats DNS poisoning — the blocking mechanism DPI desync can't
/// touch, because the ISP returns a forged IP before any TLS handshake happens. The original
/// per-adapter config is read from the registry (language-agnostic) and saved to a backup file so a
/// crash can't strand the machine on a foreign resolver — startup restores it.
/// </summary>
public static class DnsManager
{
    // Cloudflare — reachable from RU, fast, first-class DoH support.
    private static readonly string[] V4 = { "1.1.1.1", "1.0.0.1" };
    private static readonly string[] V6 = { "2606:4700:4700::1111", "2606:4700:4700::1001" };
    private const string DohTemplate = "https://cloudflare-dns.com/dns-query";

    private static string BackupFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DashConnect", "dns-backup.json");

    private sealed record AdapterDns(string Name, string Guid, string V4, string V6);

    public static bool HasPendingBackup => File.Exists(BackupFile);

    /// <summary>Back up current DNS, then point every active adapter at Cloudflare DoH.</summary>
    public static async Task ApplyAsync(CancellationToken ct = default)
    {
        try
        {
            var adapters = ActiveAdapters();

            // Capture originals ONCE — never overwrite a real backup with our own Cloudflare values.
            if (!File.Exists(BackupFile))
            {
                var backup = new List<AdapterDns>();
                foreach (var (name, guid) in adapters)
                    backup.Add(new AdapterDns(name, guid,
                        await ReadNameServerAsync("Tcpip", guid, ct),
                        await ReadNameServerAsync("Tcpip6", guid, ct)));
                Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
                await File.WriteAllTextAsync(BackupFile, JsonSerializer.Serialize(backup), ct);
            }

            // Register DoH templates so Windows 11 encrypts queries to these resolvers (idempotent).
            foreach (var s in V4.Concat(V6))
                await NetshAsync($"dns add encryption server={s} dohtemplate={DohTemplate} autoupgrade=yes udpfallback=no", ct);

            foreach (var (name, _) in adapters)
            {
                await NetshAsync($"interface ipv4 set dnsservers name=\"{name}\" static {V4[0]} primary", ct);
                await NetshAsync($"interface ipv4 add dnsservers name=\"{name}\" {V4[1]} index=2", ct);
                // IPv6 best-effort: harmless failure on adapters without IPv6.
                await NetshAsync($"interface ipv6 set dnsservers name=\"{name}\" static {V6[0]} primary", ct);
                await NetshAsync($"interface ipv6 add dnsservers name=\"{name}\" {V6[1]} index=2", ct);
            }
            await FlushAsync(ct);
            Log.Info("dns", $"включён зашифрованный DNS (Cloudflare DoH) на {adapters.Count} адаптер(ах)");
        }
        catch (Exception ex) { Log.Warn("dns", $"не удалось включить чистый DNS: {ex.Message}"); }
    }

    /// <summary>Restore the exact DNS config captured by <see cref="ApplyAsync"/>. No-op if none.</summary>
    public static async Task RevertAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(BackupFile)) return; // we never applied — leave the system untouched
            var backup = JsonSerializer.Deserialize<List<AdapterDns>>(await File.ReadAllTextAsync(BackupFile, ct))
                         ?? new List<AdapterDns>();
            foreach (var a in backup)
            {
                await RestoreFamilyAsync("ipv4", a.Name, a.V4, ct);
                await RestoreFamilyAsync("ipv6", a.Name, a.V6, ct);
            }
            await FlushAsync(ct);
            try { File.Delete(BackupFile); } catch { }
            Log.Info("dns", "DNS возвращён на исходный");
        }
        catch (Exception ex) { Log.Warn("dns", $"не удалось вернуть DNS: {ex.Message}"); }
    }

    private static async Task RestoreFamilyAsync(string family, string name, string nameserver, CancellationToken ct)
    {
        var addrs = nameserver.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (addrs.Length == 0)
        {
            await NetshAsync($"interface {family} set dnsservers name=\"{name}\" dhcp", ct); // original was automatic
            return;
        }
        await NetshAsync($"interface {family} set dnsservers name=\"{name}\" static {addrs[0]} primary", ct);
        for (int i = 1; i < addrs.Length; i++)
            await NetshAsync($"interface {family} add dnsservers name=\"{name}\" {addrs[i]} index={i + 1}", ct);
    }

    /// <summary>Read the static NameServer registry value (empty string = adapter uses DHCP DNS).</summary>
    private static async Task<string> ReadNameServerAsync(string svc, string guid, CancellationToken ct)
    {
        var path = $@"HKLM\SYSTEM\CurrentControlSet\Services\{svc}\Parameters\Interfaces\{guid}";
        var outp = await RunCaptureAsync("reg", $"query \"{path}\" /v NameServer", ct);
        foreach (var line in outp.Split('\n'))
        {
            int idx = line.IndexOf("REG_SZ", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && line.Contains("NameServer", StringComparison.OrdinalIgnoreCase))
                return line[(idx + 6)..].Trim();
        }
        return "";
    }

    /// <summary>Connected Ethernet/Wi-Fi adapters that carry a real IPv4 default gateway.</summary>
    private static IReadOnlyList<(string Name, string Guid)> ActiveAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .Select(n => (n.Name, n.Id))
            .ToList();

    private static Task FlushAsync(CancellationToken ct) => RunAsync("ipconfig", "/flushdns", ct);
    private static Task NetshAsync(string args, CancellationToken ct) => RunAsync("netsh", args, ct);

    private static async Task RunAsync(string file, string args, CancellationToken ct)
    {
        try { await RunCaptureAsync(file, args, ct); } catch { }
    }

    private static async Task<string> RunCaptureAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return "";
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return stdout;
    }
}
