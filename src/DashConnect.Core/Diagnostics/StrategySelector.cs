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
                candCts.CancelAfter(TimeSpan.FromSeconds(9));
                try
                {
                    if (await _zapret.StartAsync(zapretRoot, strategy, candCts.Token))
                    {
                        await Task.Delay(400, candCts.Token); // let WinDivert filters settle
                        report = await _tester.RunSelectionAsync(candCts.Token);
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

            // Stop early ONLY on a preset that opens everything AND is fast (a clearly excellent
            // result — nothing better to find). If it merely "passes" but is slow/borderline, keep
            // testing the rest so we settle on the genuinely best strategy, not the first pass.
            if (report.AllCriticalOpen && report.AverageHandshakeMs < 900)
            {
                onFraction?.Invoke(1);
                onProgress($"Выбрана «{strategy.Name}» — всё открылось, быстро");
                return new SelectionResult(strategy, baseline, evaluations, DirectAlreadyOpen: false);
            }
        }

        onFraction?.Invoke(1);

        // 3. Nobody was perfect — take the one that opened the most critical services (then fastest).
        var best = evaluations
            .OrderByDescending(e => CriticalOpen(e.Report))
            .ThenByDescending(e => e.Report.TotalScore)
            .ThenBy(e => e.Report.AverageHandshakeMs)
            .FirstOrDefault();

        if (best is null)
        {
            onProgress("Не удалось проверить ни одну стратегию");
            return new SelectionResult(null, baseline, evaluations, DirectAlreadyOpen: false);
        }

        onProgress($"Выбрана «{best.Strategy.Name}» (лучшая из {evaluations.Count})");
        return new SelectionResult(best.Strategy, baseline, evaluations, DirectAlreadyOpen: false);
    }

    private static int CriticalOpen(DiagnosticsReport r)
        => r.Results.Count(x => x.Critical && x.Verdict == ServiceVerdict.Open);
}
