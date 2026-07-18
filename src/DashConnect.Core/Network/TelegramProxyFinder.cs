using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DashConnect.Core.Logging;

namespace DashConnect.Core.Network;

/// <summary>One MTProto proxy (server/port/secret) plus its ready-made tg:// enable link.</summary>
public sealed record MtProxy(string Server, int Port, string Secret)
{
    public string TgLink => $"tg://proxy?server={Server}&port={Port}&secret={Secret}";
}

/// <summary>
/// Last-resort fallback for Telegram: fetches public MTProto proxy lists (RU-targeted,
/// auto-updated open-source repos), then TCP-tests them from the user's own machine — the only real
/// proof they survive the ISP's IP block — and returns the first that connects.
/// </summary>
public static class TelegramProxyFinder
{
    private static readonly (string raw, string mirror)[] Sources =
    {
        ("https://raw.githubusercontent.com/shablin/mtproto-proxy/main/data/valid_proxy.json",
         "https://cdn.jsdelivr.net/gh/shablin/mtproto-proxy@main/data/valid_proxy.json"),
        ("https://raw.githubusercontent.com/SoliSpirit/mtproto/master/all_proxies.txt",
         "https://cdn.jsdelivr.net/gh/SoliSpirit/mtproto@master/all_proxies.txt"),
        ("https://raw.githubusercontent.com/Argh94/Proxy-List/main/MTProto.txt",
         "https://cdn.jsdelivr.net/gh/Argh94/Proxy-List@main/MTProto.txt"),
    };

    // Scans raw text for tg://proxy / t.me/proxy links (works for both the .txt and .json feeds).
    private static readonly Regex LinkRx = new(
        @"(?:tg://|https?://t\.me/|https?://telegram\.me/)proxy\?server=([^&\s""']+)&port=(\d{1,5})&secret=([0-9a-zA-Z_\-=]{16,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<MtProxy?> FindWorkingAsync(Action<string>? status = null, CancellationToken ct = default)
    {
        status?.Invoke("Ищу рабочий MTProto-прокси…");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DashConnect");

        var candidates = new List<MtProxy>();
        foreach (var (raw, mirror) in Sources)
        {
            var text = await TryGetAsync(http, raw, ct) ?? await TryGetAsync(http, mirror, ct);
            if (text is null) continue;
            candidates.AddRange(Parse(text));
            if (candidates.Count > 400) break;
        }

        candidates = candidates
            .GroupBy(p => $"{p.Server}:{p.Port}")
            .Select(g => g.First())
            .ToList();
        Log.Info("tgproxy", $"собрано {candidates.Count} кандидатов, тестирую…");

        // TCP-test in parallel batches; a connect from the user's machine filters RKN IP-blocks.
        foreach (var batch in candidates.Chunk(25))
        {
            if (ct.IsCancellationRequested) break;
            var results = await Task.WhenAll(batch.Select(async p => (p, ok: await TcpOkAsync(p.Server, p.Port, ct))));
            var good = results.FirstOrDefault(r => r.ok).p;
            if (good is not null)
            {
                status?.Invoke($"Прокси найден: {good.Server}");
                return good;
            }
        }
        return null;
    }

    private static async Task<string?> TryGetAsync(HttpClient http, string url, CancellationToken ct)
    {
        try { return await http.GetStringAsync(url, ct); }
        catch { return null; }
    }

    private static IEnumerable<MtProxy> Parse(string text)
    {
        foreach (Match m in LinkRx.Matches(text))
            if (int.TryParse(m.Groups[2].Value, out var port) && port is > 0 and < 65536)
                yield return new MtProxy(m.Groups[1].Value, port, m.Groups[3].Value);
    }

    private static async Task<bool> TcpOkAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2500);
            using var c = new TcpClient();
            await c.ConnectAsync(host, port, cts.Token);
            return c.Connected;
        }
        catch { return false; }
    }
}
