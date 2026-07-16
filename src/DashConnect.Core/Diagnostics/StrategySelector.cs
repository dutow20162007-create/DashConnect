using DashConnect.Core.Logging;
using DashConnect.Core.Models;
using DashConnect.Core.Zapret;

namespace DashConnect.Core.Diagnostics;

/// <summary>
/// The dynamic configuration picker. It baselines direct connectivity, then launches winws with
/// each candidate strategy in turn, probes the critical services through it, and returns the first
/// strategy that makes Discord + YouTube + Telegram all reachable (early accept). If none is
/// perfect, it returns the highest-scoring one. Trials use fast TLS-only probes for speed.
/// </summary>
public sealed class StrategySelector
{
    private readonly ZapretManager _zapret;
    private readonly ConnectivityTester _tester;

    private const int WinwsWarmupMs = 800;

    public StrategySelector(ZapretManager zapret, ConnectivityTester tester)
    {
        _zapret = zapret;
        _tester = tester;
    }

    public sealed record SelectionResult(
        ZapretStrategy? Best,
        DiagnosticsReport Baseline,
        IReadOnlyList<StrategyEvaluation> Evaluations,
        bool DirectAlreadyOpen);

    public async Task<SelectionResult> SelectBestAsync(
        string zapretRoot,
        IReadOnlyList<ZapretStrategy> candidates,
        Action<string> onProgress,
        Action<double>? onFraction = null,
        CancellationToken ct = default)
    {
        // 1. Baseline — is a bypass even needed right now?
        onProgress("Проверяю прямое соединение…");
        await _zapret.StopAsync(ct);
        await ZapretManager.KillOrphansAsync(ct);
        var baseline = await _tester.RunSelectionAsync(ct);
        Log.Info("selector", $"baseline: {CriticalOpen(baseline)}/{baseline.Results.Count} open");

        if (baseline.AllCriticalOpen)
        {
            onProgress("Прямое соединение уже работает — обход не требуется");
            return new SelectionResult(null, baseline, Array.Empty<StrategyEvaluation>(), DirectAlreadyOpen: true);
        }

        // 2. Sweep candidates, accept the first that makes every critical service open.
        var evaluations = new List<StrategyEvaluation>();
        int index = 0;
        foreach (var strategy in candidates)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            onFraction?.Invoke((double)(index - 1) / candidates.Count);
            int left = candidates.Count - index;
            onProgress($"Проверяю «{strategy.Name}» — {index}/{candidates.Count}" +
                       (left > 0 ? $", осталось {left}" : ""));

            // Hard per-preset timeout: never let a wedged winws/WinDivert freeze the whole app.
            DiagnosticsReport? report = null;
            using (var candCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // Warmup + 2 strict rounds (WS+Hello upgrade, two real GETs) need real headroom.
                candCts.CancelAfter(TimeSpan.FromSeconds(24));
                try
                {
                    if (await _zapret.StartAsync(zapretRoot, strategy, candCts.Token))
                    {
                        await _tester.WarmupAsync(candCts.Token);          // prime WinDivert filters, discard result
                        await Task.Delay(WinwsWarmupMs, candCts.Token);    // let per-flow desync state settle
                        report = await _tester.RunSelectionAsync(candCts.Token); // 2 strict rounds, worst-of
                    }
                    else
                    {
                        Log.Warn("selector", $"«{strategy.Name}»: winws не запустился");
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Log.Warn("selector", $"«{strategy.Name}» пропущен (таймаут)");
                }
                finally
                {
                    await _zapret.StopAsync(CancellationToken.None); // always clean up
                }
            }
            if (report is null) continue;

            var eval = new StrategyEvaluation { Strategy = strategy, Report = report };
            evaluations.Add(eval);
            Log.Info("selector",
                $"'{strategy.Name}': {CriticalOpen(report)}/{report.Results.Count} open, avg {report.AverageHandshakeMs:F0}ms");

            // EARLY-ACCEPT: candidates are ordered strong/fast-first, so the first preset that clears
            // EVERY critical service (incl. the Discord gateway op-10 Hello) is good enough — take it and
            // stop, instead of grinding through all ~50 (each costs ~6-24s). If none fully passes, the
            // loop finishes and the ranking below still picks the objective best.
            if (GatewayWorks(report) && report.AllCriticalOpen)
            {
                onFraction?.Invoke(1);
                onProgress($"Найдена рабочая стратегия «{strategy.Name}» — беру её");
                Log.Info("selector", $"early-accept «{strategy.Name}» after {index}/{candidates.Count}");
                return new SelectionResult(strategy, baseline, evaluations, DirectAlreadyOpen: false);
            }
        }

        onFraction?.Invoke(1);

        // 3. Rank: a preset that carries the Discord gateway (op-10 Hello) always beats one that
        //    can't, even if the latter is faster — that gateway is exactly what users need.
        var best = evaluations
            .OrderByDescending(e => GatewayWorks(e.Report))
            .ThenByDescending(e => CriticalOpen(e.Report))
            .ThenByDescending(e => e.Report.TotalScore)
            .ThenBy(e => e.Report.AverageHandshakeMs)
            .FirstOrDefault();

        if (best is null)
        {
            onProgress("Не удалось проверить ни одну стратегию");
            return new SelectionResult(null, baseline, evaluations, DirectAlreadyOpen: false);
        }

        // Be honest when even the best preset didn't truly clear Discord's gateway.
        onProgress(GatewayWorks(best.Report)
            ? $"Выбрана «{best.Strategy.Name}» (лучшая из {evaluations.Count})"
            : $"Выбрана «{best.Strategy.Name}», но Discord прошёл не полностью — попробуйте другой сервер/провайдера");
        return new SelectionResult(best.Strategy, baseline, evaluations, DirectAlreadyOpen: false);
    }

    private static int CriticalOpen(DiagnosticsReport r)
        => r.Results.Count(x => x.Critical && x.Verdict == ServiceVerdict.Open);

    /// <summary>True when the Discord gateway WebSocket (HTTP 101 + op-10 Hello) actually came up.</summary>
    private static bool GatewayWorks(DiagnosticsReport r)
        => r.Results.Any(x => x.Host == "gateway.discord.gg" && x.Verdict == ServiceVerdict.Open);
}
