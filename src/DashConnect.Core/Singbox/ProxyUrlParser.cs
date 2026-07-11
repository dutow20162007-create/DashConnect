using System.Text;
using System.Web;
using DashConnect.Core.Logging;

namespace DashConnect.Core.Singbox;

/// <summary>
/// Parses a user-supplied proxy share link (vless:// or ss://) into a sing-box outbound object
/// tagged "game-proxy". Returns null when the URL is empty or malformed. We deliberately never
/// hardcode a server — the endpoint is always supplied by the user.
/// </summary>
public static class ProxyUrlParser
{
    public const string OutboundTag = "game-proxy";

    public static bool IsSupported(string url)
        => url.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("ss://", StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, object?>? Parse(string url, out string? error)
    {
        error = null;
        url = url.Trim();
        if (string.IsNullOrEmpty(url)) { error = "empty proxy URL"; return null; }

        try
        {
            if (url.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseVless(url);
            if (url.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) return ParseShadowsocks(url);
            error = "unsupported scheme (use vless:// or ss://)";
            return null;
        }
        catch (Exception ex)
        {
            error = $"parse error: {ex.Message}";
            Log.Warn("proxy", error);
            return null;
        }
    }

    private static Dictionary<string, object?> ParseVless(string url)
    {
        // vless://uuid@host:port?params#name
        var body = url["vless://".Length..];
        var (userinfo, host, port, query) = SplitUri(body);
        var q = HttpUtility.ParseQueryString(query);

        var outbound = new Dictionary<string, object?>
        {
            ["type"] = "vless",
            ["tag"] = OutboundTag,
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = userinfo,
        };

        var flow = q["flow"];
        if (!string.IsNullOrEmpty(flow)) outbound["flow"] = flow;

        var security = (q["security"] ?? "none").ToLowerInvariant();
        var sni = q["sni"] ?? q["peer"] ?? host;
        var fp = q["fp"];

        if (security is "tls" or "reality")
        {
            var tls = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["server_name"] = sni,
            };
            if (!string.IsNullOrEmpty(fp))
                tls["utls"] = new Dictionary<string, object?> { ["enabled"] = true, ["fingerprint"] = fp };

            if (security == "reality")
            {
                var reality = new Dictionary<string, object?> { ["enabled"] = true };
                if (!string.IsNullOrEmpty(q["pbk"])) reality["public_key"] = q["pbk"];
                if (!string.IsNullOrEmpty(q["sid"])) reality["short_id"] = q["sid"];
                tls["reality"] = reality;
            }
            outbound["tls"] = tls;
        }

        var type = (q["type"] ?? "tcp").ToLowerInvariant();
        var transport = BuildTransport(type, q, host);
        if (transport is not null) outbound["transport"] = transport;

        return outbound;
    }

    private static Dictionary<string, object?>? BuildTransport(
        string type, System.Collections.Specialized.NameValueCollection q, string host)
    {
        switch (type)
        {
            case "ws":
                var ws = new Dictionary<string, object?> { ["type"] = "ws" };
                if (!string.IsNullOrEmpty(q["path"])) ws["path"] = q["path"];
                var wsHost = q["host"] ?? host;
                ws["headers"] = new Dictionary<string, object?> { ["Host"] = wsHost };
                return ws;
            case "grpc":
                return new Dictionary<string, object?>
                {
                    ["type"] = "grpc",
                    ["service_name"] = q["serviceName"] ?? q["servicename"] ?? "",
                };
            case "http":
                var h = new Dictionary<string, object?> { ["type"] = "http" };
                if (!string.IsNullOrEmpty(q["path"])) h["path"] = q["path"];
                if (!string.IsNullOrEmpty(q["host"])) h["host"] = new[] { q["host"]! };
                return h;
            default:
                return null; // tcp — no transport block
        }
    }

    private static Dictionary<string, object?> ParseShadowsocks(string url)
    {
        // Forms:
        //   ss://base64(method:password)@host:port#name   (SIP002)
        //   ss://base64(method:password@host:port)#name   (legacy)
        var body = url["ss://".Length..];
        int hash = body.IndexOf('#');
        if (hash >= 0) body = body[..hash];
        int qmark = body.IndexOf('?');
        if (qmark >= 0) body = body[..qmark];

        string method, password, host;
        int port;

        int at = body.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = body[..at];
            var hostport = body[(at + 1)..];
            var decoded = TryBase64(userinfo) ?? userinfo;
            var colon = decoded.IndexOf(':');
            if (colon < 0) throw new FormatException("bad shadowsocks userinfo");
            method = decoded[..colon];
            password = decoded[(colon + 1)..];
            (host, port) = SplitHostPort(hostport);
        }
        else
        {
            var decoded = TryBase64(body) ?? throw new FormatException("bad shadowsocks link");
            int a2 = decoded.LastIndexOf('@');
            var creds = decoded[..a2];
            var hostport = decoded[(a2 + 1)..];
            var colon = creds.IndexOf(':');
            method = creds[..colon];
            password = creds[(colon + 1)..];
            (host, port) = SplitHostPort(hostport);
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "shadowsocks",
            ["tag"] = OutboundTag,
            ["server"] = host,
            ["server_port"] = port,
            ["method"] = method,
            ["password"] = password,
        };
    }

    private static (string userinfo, string host, int port, string query) SplitUri(string body)
    {
        int hash = body.IndexOf('#');
        if (hash >= 0) body = body[..hash];
        string query = "";
        int qmark = body.IndexOf('?');
        if (qmark >= 0) { query = body[(qmark + 1)..]; body = body[..qmark]; }

        int at = body.IndexOf('@');
        if (at < 0) throw new FormatException("missing '@' in proxy URL");
        var userinfo = body[..at];
        var (host, port) = SplitHostPort(body[(at + 1)..]);
        return (userinfo, host, port, query);
    }

    private static (string host, int port) SplitHostPort(string hostport)
    {
        // supports [ipv6]:port and host:port
        if (hostport.StartsWith('['))
        {
            int close = hostport.IndexOf(']');
            var h = hostport[1..close];
            var p = hostport[(close + 2)..];
            return (h, int.Parse(p));
        }
        int colon = hostport.LastIndexOf(':');
        if (colon < 0) throw new FormatException("missing port in proxy URL");
        return (hostport[..colon], int.Parse(hostport[(colon + 1)..]));
    }

    private static string? TryBase64(string s)
    {
        try
        {
            var norm = s.Replace('-', '+').Replace('_', '/');
            switch (norm.Length % 4) { case 2: norm += "=="; break; case 3: norm += "="; break; }
            var bytes = Convert.FromBase64String(norm);
            var text = Encoding.UTF8.GetString(bytes);
            return text.Contains(':') ? text : null;
        }
        catch { return null; }
    }
}
