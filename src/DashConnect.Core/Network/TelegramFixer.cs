namespace DashConnect.Core.Network;

/// <summary>Result of a "make Telegram work" attempt.</summary>
public sealed record TelegramFixResult(bool Ok, string? TgLink, string Message);

/// <summary>
/// Makes Telegram work without the user's own server. Tries Cloudflare WARP first (a local SOCKS the
/// app runs; Telegram traffic rides Cloudflare's network to the DCs), and if the ISP blocks WARP
/// outright, falls back to a currently-live public MTProto proxy. Either way the caller opens the
/// returned tg:// link and Telegram enables it with one click. WARP keeps running for the session.
/// </summary>
public sealed class TelegramFixer : IAsyncDisposable
{
    private readonly WarpManager _warp = new();

    public bool WarpActive => _warp.IsRunning;

    public async Task<TelegramFixResult> FixAsync(Action<string>? status = null, CancellationToken ct = default)
    {
        // 1. WARP — routes Telegram through Cloudflare, no external proxy, verified by reaching a DC.
        if (await _warp.StartAsync(status, ct))
            return new TelegramFixResult(true,
                $"tg://socks?server=127.0.0.1&port={WarpManager.SocksPort}",
                "WARP поднят. В открывшемся Telegram нажми синюю кнопку «Подключить прокси» — и всё.");

        // 2. Fallback — a public MTProto proxy that actually connects from this machine right now.
        var proxy = await TelegramProxyFinder.FindWorkingAsync(status, ct);
        if (proxy is not null)
            return new TelegramFixResult(true, proxy.TgLink,
                "Найден рабочий MTProto-прокси. В Telegram нажми «Включить прокси».");

        return new TelegramFixResult(false, null,
            "Не удалось: провайдер режет и WARP, и публичные прокси (редкий жёсткий случай). " +
            "Попробуй позже или добавь MTProto-прокси в Telegram вручную.");
    }

    public ValueTask DisposeAsync() => _warp.DisposeAsync();
}
