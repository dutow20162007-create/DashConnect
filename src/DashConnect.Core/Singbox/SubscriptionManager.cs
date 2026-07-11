using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;
using DashConnect.Core.Logging;
using DashConnect.Core.Util;

namespace DashConnect.Core.Singbox;

/// <summary>One server entry from a subscription (kept as its raw share link + friendly name).</summary>
public sealed class SingboxProfile
{
    public required string Name { get; set; }
    public required string Protocol { get; set; } // vless / shadowsocks / vmess / trojan
    public required string Raw { get; set; }       // original vless:// ss:// vmess:// trojan:// link

    public override string ToString() => $"{Name} · {Protocol}";
}

/// <summary>
/// Fetches and parses a subscription URL into a list of <see cref="SingboxProfile"/> and builds the
/// sing-box outbound for the selected one. Supports vless://, ss://, vmess:// and trojan:// links,
/// with a subscription body that may be plain or base64-encoded.
/// </summary>
public static class SubscriptionManager
{
    public const string OutboundTag = ProxyUrlParser.OutboundTag; // "game-proxy" — single proxy tag

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DashConnect/1.0");
        return http;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ---------- Subscription fetching ----------

    public static async Task<List<SingboxProfile>> FetchAsync(string subUrl, CancellationToken ct = default)
    {
        var profiles = new List<SingboxProfile>();
        subUrl = subUrl.Trim();
        if (string.IsNullOrEmpty(subUrl)) return profiles;

        // A subscription URL may itself be a single share link (paste one server).
        if (IsShareLink(subUrl))
        {
            var one = ParseProfile(subUrl);
            if (one is not null) profiles.Add(one);
            return profiles;
        }

        string body = await Http.GetStringAsync(subUrl, ct);
        var decoded = MaybeBase64(body.Trim());
        foreach (var raw in decoded.Split('\n', '\r'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var p = ParseProfile(line);
            if (p is not null) profiles.Add(p);
        }
        Log.Info("sub", $"subscription parsed: {profiles.Count} server(s)");
        return profiles;
    }

    private static bool IsShareLink(string s)
        => s.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase);

    private static string MaybeBase64(string text)
    {
        if (IsShareLink(text) || text.Contains("://")) return text;
        try
        {
            var norm = text.Replace('-', '+').Replace('_', '/').Replace("\n", "").Replace("\r", "");
            switch (norm.Length % 4) { case 2: norm += "=="; break; case 3: norm += "="; break; }
            var bytes = Convert.FromBase64String(norm);
            var decoded = Encoding.UTF8.GetString(bytes);
            return decoded.Contains("://") ? decoded : text;
        }
        catch { return text; }
    }

    // ---------- Profile parsing ----------

    public static SingboxProfile? ParseProfile(string link)
    {
        try
        {
            string proto =
                link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ? "vless" :
                link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) ? "vmess" :
                link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase) ? "trojan" :
                link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase) ? "shadowsocks" : "";
            if (proto.Length == 0) return null;

            var name = ExtractName(link, proto);
            return new SingboxProfile { Name = name, Protocol = proto, Raw = link };
        }
        catch { return null; }
    }

    private static string ExtractName(string link, string proto)
    {
        int hash = link.IndexOf('#');
        if (hash >= 0 && hash + 1 < link.Length)
            return HttpUtility.UrlDecode(link[(hash + 1)..]).Trim();

        if (proto == "vmess")
        {
            var json = TryVmessJson(link);
            if (json is not null && json.RootElement.TryGetProperty("ps", out var ps))
                return ps.GetString() ?? "vmess";
        }
        return proto;
    }

    // ---------- Outbound building ----------

    /// <summary>Builds the sing-box outbound object for a profile, tagged as the single proxy outbound.</summary>
    public static Dictionary<string, object?>? BuildOutbound(SingboxProfile profile, out string? error)
    {
        error = null;
        switch (profile.Protocol)
        {
            case "vless":
            case "shadowsocks":
                return ProxyUrlParser.Parse(profile.Raw, out error);
            case "trojan":
                return ParseTrojan(profile.Raw, out error);
            case "vmess":
                return ParseVmess(profile.Raw, out error);
            default:
                error = "unsupported protocol";
                return null;
        }
    }

    private static Dictionary<string, object?>? ParseTrojan(string url, out string? error)
    {
        error = null;
        try
        {
            var body = url["trojan://".Length..];
            int hash = body.IndexOf('#'); if (hash >= 0) body = body[..hash];
            string query = "";
            int q = body.IndexOf('?'); if (q >= 0) { query = body[(q + 1)..]; body = body[..q]; }
            int at = body.IndexOf('@');
            var password = HttpUtility.UrlDecode(body[..at]);
            var hostPort = body[(at + 1)..];
            int colon = hostPort.LastIndexOf(':');
            var host = hostPort[..colon];
            var port = int.Parse(hostPort[(colon + 1)..]);
            var p = HttpUtility.ParseQueryString(query);

            var outbound = new Dictionary<string, object?>
            {
                ["type"] = "trojan",
                ["tag"] = OutboundTag,
                ["server"] = host,
                ["server_port"] = port,
                ["password"] = password,
                ["tls"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["server_name"] = p["sni"] ?? p["peer"] ?? host,
                },
            };
            var transport = BuildTransport(p["type"] ?? "tcp", p, host);
            if (transport is not null) outbound["transport"] = transport;
            return outbound;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    private static Dictionary<string, object?>? ParseVmess(string url, out string? error)
    {
        error = null;
        try
        {
            var json = TryVmessJson(url);
            if (json is null) { error = "bad vmess payload"; return null; }
            var r = json.RootElement;
            string Get(string k) => r.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

            int port = int.TryParse(Get("port"), out var pp) ? pp : 443;
            int aid = int.TryParse(Get("aid"), out var aa) ? aa : 0;

            var outbound = new Dictionary<string, object?>
            {
                ["type"] = "vmess",
                ["tag"] = OutboundTag,
                ["server"] = Get("add"),
                ["server_port"] = port,
                ["uuid"] = Get("id"),
                ["alter_id"] = aid,
                ["security"] = "auto",
            };

            var tlsMode = Get("tls");
            var sni = Get("sni");
            var netHost = Get("host");
            if (tlsMode is "tls" or "reality")
            {
                outbound["tls"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["server_name"] = !string.IsNullOrEmpty(sni) ? sni :
                                      !string.IsNullOrEmpty(netHost) ? netHost : Get("add"),
                };
            }

            var net = Get("net");
            var query = HttpUtility.ParseQueryString("");
            query["path"] = Get("path");
            query["host"] = netHost;
            query["serviceName"] = Get("path");
            var transport = BuildTransport(net, query, Get("add"));
            if (transport is not null) outbound["transport"] = transport;
            return outbound;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    private static JsonDocument? TryVmessJson(string url)
    {
        try
        {
            var b64 = url["vmess://".Length..].Trim();
            var norm = b64.Replace('-', '+').Replace('_', '/');
            switch (norm.Length % 4) { case 2: norm += "=="; break; case 3: norm += "="; break; }
            var jsonText = Encoding.UTF8.GetString(Convert.FromBase64String(norm));
            return JsonDocument.Parse(jsonText);
        }
        catch { return null; }
    }

    private static Dictionary<string, object?>? BuildTransport(
        string type, System.Collections.Specialized.NameValueCollection q, string host)
    {
        switch ((type ?? "tcp").ToLowerInvariant())
        {
            case "ws":
                var ws = new Dictionary<string, object?> { ["type"] = "ws" };
                if (!string.IsNullOrEmpty(q["path"])) ws["path"] = q["path"];
                ws["headers"] = new Dictionary<string, object?> { ["Host"] = q["host"] ?? host };
                return ws;
            case "grpc":
                return new Dictionary<string, object?>
                {
                    ["type"] = "grpc",
                    ["service_name"] = q["serviceName"] ?? q["path"] ?? "",
                };
            default:
                return null;
        }
    }

    // ---------- Persistence ----------

    public static void Save(List<SingboxProfile> profiles)
    {
        try
        {
            Paths.EnsureDirectories();
            File.WriteAllText(ProfilesFile, JsonSerializer.Serialize(profiles, Json));
        }
        catch (Exception ex) { Log.Warn("sub", $"save profiles: {ex.Message}"); }
    }

    public static List<SingboxProfile> Load()
    {
        try
        {
            if (File.Exists(ProfilesFile))
                return JsonSerializer.Deserialize<List<SingboxProfile>>(File.ReadAllText(ProfilesFile)) ?? new();
        }
        catch (Exception ex) { Log.Warn("sub", $"load profiles: {ex.Message}"); }
        return new();
    }

    private static string ProfilesFile => Path.Combine(Paths.AppDataDir, "profiles.json");
}
