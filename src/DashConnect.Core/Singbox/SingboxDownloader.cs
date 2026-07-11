using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using DashConnect.Core.Logging;
using DashConnect.Core.Util;

namespace DashConnect.Core.Singbox;

/// <summary>
/// Automatically downloads and initializes the latest stable sing-box Windows binary from the
/// official GitHub releases into %AppData%\DashConnect\singbox. Idempotent: if a working binary is
/// already present it is reused.
/// </summary>
public sealed class SingboxDownloader
{
    private const string LatestApi = "https://api.github.com/repos/SagerNet/sing-box/releases/latest";
    private static readonly Regex AssetPattern =
        new(@"^sing-box-[\d.]+-windows-amd64\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DashConnect/1.0 (+dpi-bypass-gui)");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public bool IsInstalled => File.Exists(Paths.SingboxExe);

    /// <summary>Ensures sing-box.exe exists. Returns its path, or null on failure.</summary>
    public async Task<string?> EnsureAsync(Action<string> onProgress, CancellationToken ct = default)
    {
        Paths.EnsureDirectories();

        if (IsInstalled)
        {
            var version = await GetVersionAsync(ct);
            if (version is not null)
            {
                onProgress($"sing-box present ({version})");
                return Paths.SingboxExe;
            }
            Log.Warn("singbox", "existing binary is unusable, re-downloading");
        }

        try
        {
            onProgress("Resolving latest sing-box release…");
            var (assetUrl, tag) = await ResolveLatestAssetAsync(ct);
            if (assetUrl is null)
            {
                Log.Error("singbox", "no windows-amd64 asset found in latest release");
                return null;
            }

            onProgress($"Downloading sing-box {tag}…");
            var zipPath = Path.Combine(Paths.SingboxDir, "sing-box.zip");
            await DownloadFileAsync(assetUrl, zipPath, ct);

            onProgress("Extracting sing-box…");
            ExtractExe(zipPath);
            TryDelete(zipPath);

            if (!IsInstalled)
            {
                Log.Error("singbox", "sing-box.exe not found inside downloaded archive");
                return null;
            }

            var v = await GetVersionAsync(ct);
            onProgress($"sing-box ready ({v ?? tag})");
            Log.Info("singbox", $"installed sing-box {v ?? tag}");
            return Paths.SingboxExe;
        }
        catch (Exception ex)
        {
            Log.Error("singbox", "download failed", ex);
            onProgress($"sing-box download failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<(string? url, string tag)> ResolveLatestAssetAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync(LatestApi, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "latest" : "latest";
        if (!doc.RootElement.TryGetProperty("assets", out var assets)) return (null, tag);

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is not null && AssetPattern.IsMatch(name))
            {
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                return (url, tag);
            }
        }
        return (null, tag);
    }

    private static async Task DownloadFileAsync(string url, string destination, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destination);
        await src.CopyToAsync(dst, ct);
    }

    private static void ExtractExe(string zipPath)
    {
        var extractDir = Path.Combine(Paths.SingboxDir, "extract");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);

        ZipFile.ExtractToDirectory(zipPath, extractDir);
        var exe = Directory.EnumerateFiles(extractDir, "sing-box.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (exe is null) return;

        File.Copy(exe, Paths.SingboxExe, overwrite: true);
        TryDeleteDir(extractDir);
    }

    private static async Task<string?> GetVersionAsync(CancellationToken ct)
    {
        var (code, output) = await ProcessUtil.RunAsync(Paths.SingboxExe, "version", TimeSpan.FromSeconds(8), ct);
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return null;
        return output.Split('\n').FirstOrDefault()?.Trim();
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static void TryDeleteDir(string path) { try { Directory.Delete(path, true); } catch { } }
}
