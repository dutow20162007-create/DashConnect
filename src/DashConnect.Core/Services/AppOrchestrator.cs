using DashConnect.Core.Config;
using DashConnect.Core.Diagnostics;
using DashConnect.Core.Logging;
using DashConnect.Core.Models;
using DashConnect.Core.Network;
using DashConnect.Core.Singbox;
using DashConnect.Core.Zapret;

namespace DashConnect.Core.Services;

/// <summary>
/// The central coordinator behind the global Connect/Disconnect toggle. It sequences orphan
/// cleanup, diagnostics, dynamic strategy selection, the Zapret (winws) engine and the Sing-box
/// game-routing engine, and raises events the GUI binds to.
/// </summary>
public sealed class AppOrchestrator : IAsyncDisposable
{
    private readonly ZapretManager _zapret = new();
    private readonly SingboxManager _singbox = new();
    private readonly ConnectivityTester _tester = new();
    private readonly SingboxDownloader _downloader = new();
    private readonly StrategySelector _selector;
    private readonly TgWsProxyManager _tgws = new();
    private readonly TelegramFixer _telegramFixer = new();

    private CancellationTokenSource? _cts;
    private readonly object _ctsLock = new();
    private readonly SemaphoreSlim _opGate = new(1, 1);
    private bool _dnsApplied;

    public AppOrchestrator()
    {
        _selector = new StrategySelector(_zapret, _tester);
    }

    public EngineState State { get; private set; } = EngineState.Stopped;
    public ZapretStrategy? ActiveStrategy => _zapret.Current;
    public bool DpiActive => _zapret.IsRunning;
    public bool GameRoutingActive => _singbox.IsRunning;
    public bool IsConnected => _zapret.IsRunning || _singbox.IsRunning;

    /// <summary>The Telegram WebSocket bridge is running (tg-ws-proxy on 127.0.0.1:1443).</summary>
    public bool TelegramProxyActive => _tgws.IsRunning;

    /// <summary>tg:// link Telegram opens to enable the bridge — valid only while it is running.</summary>
    public string TelegramProxyLink => _tgws.TgLink;

    public event Action<string>? StatusChanged;
    public event Action<double>? ProgressChanged;
    public event Action<EngineState>? StateChanged;
    public event Action<DiagnosticsReport>? DiagnosticsUpdated;
    public event Action<ZapretStrategy?>? StrategySelected;

    public sealed record ConnectionOutcome(
        bool DpiActive,
        ZapretStrategy? Strategy,
        bool GameRoutingActive,
        string? GameRoutingError,
        DiagnosticsReport? FinalReport,
        bool DirectAlreadyOpen);

    /// <summary>Runs the full connect sequence for the currently enabled modules.</summary>
    public async Task<ConnectionOutcome> ConnectAsync(AppConfig config, CancellationToken external = default)
    {
        await _opGate.WaitAsync(external);
        CancellationToken ct;
        lock (_ctsLock)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            ct = _cts.Token;
        }
        try
        {
            SetState(EngineState.Starting);
            Status("Подготовка… очищаю прошлые сессии");
            await ZapretManager.KillOrphansAsync(ct);
            await SingboxManager.KillOrphansAsync(ct);

            ZapretStrategy? chosen = null;
            bool directOpen = false;
            DiagnosticsReport? finalReport;

            bool needZapret = config.DpiBypassEnabled || config.GameDpiEnabled;

            // ---- Subsystem A: Zapret / DPI bypass ----
            if (needZapret)
            {
                if (!StrategyProvider.IsValidRoot(config.ZapretRoot))
                {
                    Status($"Zapret не найден: {config.ZapretRoot}");
                    SetState(EngineState.Error);
                    return new ConnectionOutcome(false, null, false, "неверный путь к Zapret", null, false);
                }

                // IMPORTANT: scan ALWAYS with the game filter OFF. GameFilter=All makes winws intercept
                // every high port (all UDP/TCP 1024-65535); doing that repeatedly during a preset sweep
                // can wedge a live network. The game filter is applied only to the FINAL chosen preset
                // below (a single persistent winws — safe, like double-clicking the .bat).
                var scanStrategies = StrategyProvider.LoadAll(config.ZapretRoot, GameFilterMode.Disabled);

                if (config.AutoSelect && string.IsNullOrWhiteSpace(config.PreferredStrategy))
                {
                    SetState(EngineState.Testing);
                    var selection = await _selector.SelectBestAsync(
                        config.ZapretRoot, scanStrategies.ToList(), Status, Progress, ct);
                    directOpen = selection.DirectAlreadyOpen;
                    chosen = selection.Best;
                }

                chosen ??= scanStrategies.FirstOrDefault(s =>
                               s.Name.Equals(config.PreferredStrategy, StringComparison.OrdinalIgnoreCase))
                           ?? scanStrategies.FirstOrDefault(s =>
                               s.Name.Equals(StrategyProvider.DefaultStrategyName, StringComparison.OrdinalIgnoreCase))
                           ?? scanStrategies.FirstOrDefault();

                if (chosen is not null && !directOpen)
                {
                    // Final launch: apply the game filter (if enabled) to this one preset only.
                    var gameFilter = config.GameDpiEnabled ? GameFilterMode.All : GameFilterMode.Disabled;
                    ZapretSettings.ApplyGameFilter(config.ZapretRoot, gameFilter);
                    var launch = config.GameDpiEnabled
                        ? StrategyProvider.LoadAll(config.ZapretRoot, gameFilter)
                              .FirstOrDefault(s => s.Name == chosen.Name) ?? chosen
                        : chosen;

                    Status($"Запускаю «{launch.Name}»…");
                    await _zapret.StartAsync(config.ZapretRoot, launch, ct);
                }

                StrategySelected?.Invoke(_zapret.Current);
            }

            // ---- Subsystem B: VPN (sing-box tunnel via subscription) ----
            string? vpnError = null;
            if (config.VpnEnabled)
                vpnError = await StartVpnAsync(config, ct);

            // ---- Subsystem C: encrypted DNS (defeats DNS poisoning that DPI can't reach) ----
            // Applied whenever the user wants it — not gated on winws timing, so it never silently
            // no-ops. Verified inside ApplyAsync.
            bool dnsOk = false;
            if (config.CleanDnsEnabled)
            {
                Status("Включаю зашифрованный DNS…");
                dnsOk = await DnsManager.ApplyAsync(ct);
                _dnsApplied = true;
            }

            // ---- Subsystem D: Telegram WebSocket bridge (Flowseal tg-ws-proxy) ----
            // Telegram talks to its DCs by IP (MTProto), so plain DPI desync doesn't fix it. The bridge
            // wraps that traffic in WebSocket/TLS to Telegram's own domains — plain HTTPS to the DPI,
            // no third-party server. Start it whenever enabled so Telegram keeps working once configured.
            if (config.TelegramFixEnabled)
            {
                var secret = EnsureTelegramSecret(config);
                Status("Поднимаю мост Telegram…");
                await _tgws.StartAsync(config.ZapretRoot, secret, Status, ct);
            }

            // ---- Final verification ----
            Status("Проверка соединения…");
            finalReport = await _tester.RunDefaultAsync(ct);
            DiagnosticsUpdated?.Invoke(finalReport);

            var connected = _zapret.IsRunning || _singbox.IsRunning || (needZapret && directOpen);
            SetState(connected ? EngineState.Running : EngineState.Error);
            var summary = BuildSummary(finalReport, directOpen);
            if (config.CleanDnsEnabled)
                summary += dnsOk ? "  •  DNS 1.1.1.1 (DoH)" : "  •  DNS не применён";
            Status(summary);

            return new ConnectionOutcome(
                _zapret.IsRunning, _zapret.Current, _singbox.IsRunning, vpnError, finalReport, directOpen);
        }
        catch (OperationCanceledException)
        {
            Status("Соединение отменено");
            await StopAllAsync(CancellationToken.None);
            SetState(EngineState.Stopped);
            return new ConnectionOutcome(false, null, false, "отменено", null, false);
        }
        catch (Exception ex)
        {
            Log.Error("orchestrator", "connect failed", ex);
            Status($"Ошибка: {ex.Message}");
            await StopAllAsync(CancellationToken.None);
            SetState(EngineState.Error);
            return new ConnectionOutcome(false, null, false, ex.Message, null, false);
        }
        finally
        {
            lock (_ctsLock) { _cts?.Dispose(); _cts = null; }
            _opGate.Release();
        }
    }

    private async Task<string?> StartVpnAsync(AppConfig config, CancellationToken ct)
    {
        // Prefer already-fetched profiles; otherwise fetch from the subscription URL.
        var profiles = SubscriptionManager.Load();
        if (profiles.Count == 0 && !string.IsNullOrWhiteSpace(config.SubscriptionUrl))
        {
            Status("Загружаю подписку…");
            try
            {
                profiles = await SubscriptionManager.FetchAsync(config.SubscriptionUrl, ct);
                SubscriptionManager.Save(profiles);
            }
            catch (Exception ex)
            {
                Status($"Подписка недоступна: {ex.Message}");
                return "подписка недоступна";
            }
        }
        if (profiles.Count == 0)
        {
            Status("Нет серверов — добавьте подписку в Настройках");
            return "нет серверов";
        }

        var profile = profiles.FirstOrDefault(p => p.Name == config.SelectedProfileName) ?? profiles[0];

        Status("Готовлю sing-box…");
        var exe = await _downloader.EnsureAsync(Status, ct);
        if (exe is null) return "бинарник sing-box недоступен";

        var routes = GameRoutesStore.Load();
        var build = SingboxTunnelBuilder.BuildAndSave(profile, config.VpnFull, routes);
        if (!build.Ok) { Status($"Ошибка конфигурации: {build.Error}"); return build.Error; }

        Status($"Запускаю VPN через «{profile.Name}»…");
        var ok = await _singbox.StartAsync(exe, DashConnect.Core.Util.Paths.SingboxConfig, ct);
        if (!ok) return "sing-box не запустился (см. журнал)";

        Status($"VPN активен: {profile.Name}");
        return null;
    }

    /// <summary>
    /// "Make Telegram work": brings up the bundled WebSocket bridge (primary — no external server) and
    /// returns the tg:// link to hand to Telegram Desktop. If the bridge can't run (exe missing, port
    /// busy), falls back to Cloudflare WARP, then a live public MTProto proxy.
    /// </summary>
    public async Task<TelegramFixResult> FixTelegramAsync(AppConfig config, Action<string>? status = null, CancellationToken ct = default)
    {
        if (config.TelegramFixEnabled)
        {
            var secret = EnsureTelegramSecret(config);
            if (await _tgws.StartAsync(config.ZapretRoot, secret, status, ct))
                return new TelegramFixResult(true, _tgws.TgLink,
                    "Мост Telegram поднят. В открывшемся Telegram нажми «Подключить прокси» — и всё.");
        }
        return await _telegramFixer.FixAsync(status, ct);
    }

    /// <summary>Returns the persisted MTProto secret, generating and storing one on first use.</summary>
    private static string EnsureTelegramSecret(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.TgWsProxySecret) || config.TgWsProxySecret.Length != 32)
            config.TgWsProxySecret = TgWsProxyManager.NewSecret();
        return config.TgWsProxySecret;
    }

    public async Task DisconnectAsync()
    {
        lock (_ctsLock) { try { _cts?.Cancel(); } catch { } }
        await _opGate.WaitAsync();
        try
        {
            Status("Отключаюсь…");
            await StopAllAsync(CancellationToken.None);
            SetState(EngineState.Stopped);
            Status("Отключено");
            StrategySelected?.Invoke(null);
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <summary>Runs diagnostics against the currently-active configuration without changing it.</summary>
    public async Task<DiagnosticsReport> RunDiagnosticsAsync(CancellationToken ct = default)
    {
        var report = await _tester.RunDefaultAsync(ct);
        DiagnosticsUpdated?.Invoke(report);
        return report;
    }

    private async Task StopAllAsync(CancellationToken ct)
    {
        if (_dnsApplied)
        {
            await DnsManager.RevertAsync(CancellationToken.None);
            _dnsApplied = false;
        }
        _tgws.Stop();
        await _zapret.StopAsync(ct);
        await _singbox.StopAsync(ct);
        await ZapretManager.KillOrphansAsync(ct);
        await SingboxManager.KillOrphansAsync(ct);
    }

    private static string BuildSummary(DiagnosticsReport report, bool directOpen)
    {
        if (directOpen) return "Прямое соединение работает — защита в режиме ожидания";
        int open = report.OpenCount, total = report.Results.Count;
        return open == total
            ? $"Защита включена — доступны все {total} сервис(ов)"
            : $"Защита включена — доступно {open} из {total}";
    }

    private void SetState(EngineState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private void Status(string message)
    {
        Log.Info("status", message);
        StatusChanged?.Invoke(message);
    }

    private void Progress(double fraction) => ProgressChanged?.Invoke(fraction);

    public async ValueTask DisposeAsync()
    {
        try { await StopAllAsync(CancellationToken.None); } catch { }
        await _zapret.DisposeAsync();
        await _singbox.DisposeAsync();
        await _tgws.DisposeAsync();
        await _telegramFixer.DisposeAsync();
        _opGate.Dispose();
    }
}
