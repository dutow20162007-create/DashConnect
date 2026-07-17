using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DashConnect.Core.Logging;

namespace DashConnect.Core.Update;

public sealed record UpdateInfo(string Version, string DownloadUrl, string Notes);

/// <summary>
/// Checks GitHub Releases for a newer version and downloads the installer. The current version is a
/// single source of truth used both here and by the MSI/product build.
/// </summary>
public static class UpdateChecker
{
    public const string CurrentVersion = "1.1.11";
    public const string Owner = "dutow20162007-create";
    public const string Repo = "DashConnect";

    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases/latest";

    /// <summary>Returns update info if a newer release exists, otherwise null (never throws). Tries
    /// the rich GitHub API first, then a github.com redirect (more reachable on RU ISPs).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var latest = await LatestViaApiAsync(ct) ?? await LatestViaRedirectAsync(ct);
        if (latest is null) return null;
        return IsNewer(latest.Version, CurrentVersion) ? latest : null;
    }

    /// <summary>Latest release via api.github.com (rich: real asset URL + notes). Null on failure.</summary>
    private static async Task<UpdateInfo?> LatestViaApiAsync(CancellationToken ct)
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(15));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = (root.TryGetProperty("tag_name", out var t) ? t.GetString() : null)?.TrimStart('v', 'V') ?? "";
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            string? msi = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    {
                        msi = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            return new UpdateInfo(tag, msi ?? ReleasesPage, notes);
        }
        catch (Exception ex)
        {
            Log.Debug("update", $"api.github.com недоступен: {ex.Message}");
            return null;
        }
    }

    /// <summary>Fallback: read the tag from the github.com/…/releases/latest redirect and build the
    /// asset URL from the known naming pattern. api.github.com is often blocked in RU; github.com
    /// (especially with the DPI bypass on) is far more reachable.</summary>
    private static async Task<UpdateInfo?> LatestViaRedirectAsync(CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DashConnect", CurrentVersion));
            var resp = await http.GetAsync($"https://github.com/{Owner}/{Repo}/releases/latest", ct);
            var location = resp.Headers.Location?.ToString() ?? "";
            var m = Regex.Match(location, @"/tag/v?([0-9][0-9.]*)");
            if (!m.Success) return null;
            var tag = m.Groups[1].Value;
            var msi = $"https://github.com/{Owner}/{Repo}/releases/download/v{tag}/DashConnect-{tag}.msi";
            return new UpdateInfo(tag, msi, "");
        }
        catch (Exception ex)
        {
            Log.Debug("update", $"github.com редирект недоступен: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads the installer to a temp file and returns its path (null on failure).</summary>
    public static async Task<string?> DownloadAsync(string url, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), $"DashConnect-{Guid.NewGuid():N}.msi");
            using var http = NewClient(TimeSpan.FromMinutes(10));
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(dest);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
            return dest;
        }
        catch (Exception ex)
        {
            Log.Warn("update", $"скачивание обновления не удалось: {ex.Message}");
            return null;
        }
    }

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DashConnect", CurrentVersion));
        return http;
    }

    /// <summary>Numeric dotted-version comparison (1.2.0 &gt; 1.1.9); non-numeric parts count as 0.</summary>
    private static bool IsNewer(string remote, string local)
    {
        static int[] Parse(string v) =>
            v.Split('.', '-', '+').Select(p => int.TryParse(p, out var num) ? num : 0).ToArray();
        var r = Parse(remote);
        var l = Parse(local);
        for (int i = 0; i < Math.Max(r.Length, l.Length); i++)
        {
            int rv = i < r.Length ? r[i] : 0, lv = i < l.Length ? l[i] : 0;
            if (rv != lv) return rv > lv;
        }
        return false;
    }
}
