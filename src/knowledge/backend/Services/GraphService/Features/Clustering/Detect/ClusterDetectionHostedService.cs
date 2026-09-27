using GraphService.Domain.Ports;

namespace GraphService.Features.Clustering.Detect;

// FR-17, FR-18, SC-10, SC-18, ADR-0035 決定 3, ADR-0083 決定 1, [[IADR-0425]] (#1363):
// クラスタ検出の**日次バッチ**。
//
// 計画 ADR-0035 決定 3 が実行タイミングを確定している ——
// 「実行タイミングは**定期バッチ（日次）**とする。……**取り込みごとの再計算は要約の再生成が
//   連鎖して費用が読めない**」。したがって周期は 24 時間であり、**文書の更新ごとに走らせない。**
//
// 形は `KnowledgeHealthHostedService` に合わせる（`BackgroundService` ＋ `PeriodicTimer` ＋
// 初回は 1 周期後 ＋ 排他リースのゲート ＋ `TryRunCycleAsync` を internal にして決定的に検証）。
public sealed class ClusterDetectionHostedService(
    IServiceScopeFactory scopeFactory,
    IClusterDetectionLeaseCoordinator leaseCoordinator,
    ILogger<ClusterDetectionHostedService> logger) : BackgroundService
{
    // ADR-0035 決定 3: **日次**。
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    // #1598: 周期の実際の長さ。**試験だけが短くする**（周期は待てない）。本番の組み立ては触らない。
    internal TimeSpan CycleInterval { get; init; } = Interval;

    // #1622: 周期の拍の源。**試験だけが偽の時計（FakeTimeProvider）に差し替える**（壁時計の間隔は負荷で揺れる）。本番はシステムの時計のまま。
    internal TimeProvider CycleClock { get; init; } = TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CycleInterval, CycleClock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TryRunCycleAsync(stoppingToken);
                }
                // ［2026-09-26 / #1598・[[IADR-0299]] 追記］🔴 **素通しするのは停止要求（stoppingToken）の取り消しだけである。**
                // 下流の時間切れ等の取り消しは周期の失敗であり、型だけで素通しすると外側で「シャットダウン」と読まれて
                // ループが**永久に**終わる。形は DriftDetectionHostedService（#1382）と同じ。
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // 1 周期の失敗でホストを落とさない（本サービスは DocumentUpdated /
                    // DocumentDeleted の購読者でもある。クラスタ検出の都合で購読を止めない）。
                    logger.LogError(ex, "クラスタ検出に失敗した。次周期で再試行する。");
                }
            }
        }
        // #1598: 想定外の取り消しを黙って「シャットダウン」と読まない（届いたら例外のまま出す）。
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // シャットダウン。
        }
    }

    // 🔴 単一書き手化のゲート。**リースを取得できたレプリカだけが検出する。**
    // 取得できない周期は**読み込みもしない**（スキップ＝fail-safe）。戻り値は実行したか。
    internal async Task<bool> TryRunCycleAsync(CancellationToken ct)
    {
        await using var lease = await leaseCoordinator.TryAcquireAsync(ct);
        if (lease is null)
        {
            logger.LogDebug(
                "クラスタ検出のリースを取得できなかった（他レプリカが実行中）。本周期をスキップする。");
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<ClusterDetectionJob>();
        await job.RunAsync(ct);
        return true;
    }
}
