namespace DashConnect.Core.Network;

/// <summary>Result of a "make Telegram work" attempt.</summary>
public sealed record TelegramFixResult(bool Ok, string? TgLink, string Message);

/// <summary>
/// Last-resort Telegram fallback, used when the bundled WebSocket bridge can't run: finds a
/// currently-live public MTProto proxy and returns its tg:// link. The caller opens the link and
/// Telegram enables the proxy with one click.
/// </summary>
public static class TelegramFixer
{
    public static async Task<TelegramFixResult> FixAsync(Action<string>? status = null, CancellationToken ct = default)
    {
        var proxy = await TelegramProxyFinder.FindWorkingAsync(status, ct);
        if (proxy is not null)
            return new TelegramFixResult(true, proxy.TgLink,
                "Найден рабочий MTProto-прокси. В Telegram нажми «Включить прокси».");

        return new TelegramFixResult(false, null,
            "Не удалось: рабочих публичных MTProto-прокси сейчас не найдено. " +
            "Попробуй позже или добавь MTProto-прокси в Telegram вручную.");
    }
}
