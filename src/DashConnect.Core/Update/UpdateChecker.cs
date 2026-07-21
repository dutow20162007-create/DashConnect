using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DashConnect.Core.Logging;

namespace DashConnect.Core.Update;

/// <summary><paramref name="Sha256Url"/> points at a sibling <c>&lt;asset&gt;.sha256</c> file whose
/// first 64-hex token is the expected digest of the installer; null when the release didn't publish
/// one (older releases).</summary>
public sealed record UpdateInfo(string Version, string DownloadUrl, string Notes, string? Sha256Url = null);

/// <summary>
/// Checks GitHub Releases for a newer version and downloads the installer. The current version is a
/// single source of truth used both here and by the MSI/product build.
/// </summary>
public static class UpdateChecker
{
    public const string CurrentVersion = "1.1.15";
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
            string? msi = null, sha = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        msi = a.GetProperty("browser_download_url").GetString();
                    else if (name.EndsWith(".msi.sha256", StringComparison.OrdinalIgnoreCase))
                        sha = a.GetProperty("browser_download_url").GetString();
                }
            return new UpdateInfo(tag, msi ?? ReleasesPage, notes, sha);
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
            return new UpdateInfo(tag, msi, "", msi + ".sha256");
        }
        catch (Exception ex)
        {
            Log.Debug("update", $"github.com редирект недоступен: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads the installer to a temp file and returns its path (null on failure). When the
    /// release published a <c>.sha256</c>, the file is verified against it and a MISMATCH fails the
    /// download (returns null) — we never hand a tampered/corrupt installer to msiexec.</summary>
    public static async Task<string?> DownloadAsync(
        string url, IProgress<double>? progress = null, string? sha256Url = null, CancellationToken ct = default)
    {
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), $"DashConnect-{Guid.NewGuid():N}.msi");
            using var http = NewClient(TimeSpan.FromMinutes(10));
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(dest))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
            }

            if (!string.IsNullOrWhiteSpace(sha256Url) && !await VerifyChecksumAsync(dest, sha256Url, ct))
            {
                Log.Error("update", "контрольная сумма обновления не совпала — установка отменена");
                try { File.Delete(dest); } catch { }
                return null;
            }
            return dest;
        }
        catch (Exception ex)
        {
            Log.Warn("update", $"скачивание обновления не удалось: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads the expected digest and compares it to the file's actual SHA-256. On any
    /// error fetching the checksum it returns true (fail-open — an older release without a .sha256
    /// must still be installable); only a real, confirmed mismatch returns false.</summary>
    private static async Task<bool> VerifyChecksumAsync(string file, string sha256Url, CancellationToken ct)
    {
        string expected;
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(30));
            var text = await http.GetStringAsync(sha256Url, ct);
            var m = Regex.Match(text, "[0-9a-fA-F]{64}");
            if (!m.Success) { Log.Debug("update", "в .sha256 нет валидного хеша — пропускаю проверку"); return true; }
            expected = m.Value.ToLowerInvariant();
        }
        catch (Exception ex) { Log.Debug("update", $".sha256 недоступен ({ex.Message}) — пропускаю проверку"); return true; }

        await using var fs = File.OpenRead(file);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (hash == expected) { Log.Info("update", "контрольная сумма обновления подтверждена (SHA-256)"); return true; }
        Log.Error("update", $"SHA-256 не совпал: ожидалось {expected}, получено {hash}");
        return false;
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
