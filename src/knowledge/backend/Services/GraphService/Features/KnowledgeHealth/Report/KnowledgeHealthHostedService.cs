using GraphService.Domain.Ports;

namespace GraphService.Features.KnowledgeHealth.Report;

// FR-10, FR-17, UC-05, SC-10, ADR-0006, [[IADR-0299]] 決定 3 (#443):
// ナレッジ健全性の観測値を定期的に報告するワーカー。
//
// 形は `PrivateNoteMaintenanceHostedService`（BackgroundService ＋ PeriodicTimer ＋ 初回は 1 周期後）と
// `DataSourceSyncHostedService`（リースのゲート ＋ `TryRunCycleAsync` を internal にして
// 決定的に検証）を合わせたものである。
//
// **初回実行は起動から 1 周期後**とする —— 起動直後に走らせると、テストホストの立ち上げや
// マイグレーション・seed と競合する。本番でも再起動のたびに即走らせる必要は無い
// （指標は運用の棚卸しに使うもので、分単位の鮮度を要さない）。
public sealed class KnowledgeHealthHostedService(
    IServiceScopeFactory scopeFactory,
    IKnowledgeHealthLeaseCoordinator leaseCoordinator,
    ILogger<KnowledgeHealthHostedService> logger) : BackgroundService
{
    // 周期。指標は棚卸しの材料であり、分単位の鮮度を要さない。
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TryRunCycleAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 1 周期の失敗でホストを落とさない（本サービスは DocumentUpdated /
                    // DocumentDeleted の購読者でもある。指標の都合で購読を止めない）。
                    logger.LogError(ex, "ナレッジ健全性の報告に失敗した。次周期で再試行する。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // シャットダウン。
        }
    }

    // 🔴 単一書き手化のゲート。**リースを取得できたレプリカだけが報告する。**
    // 取得できない周期は**収集もしない**（スキップ＝fail-safe）。戻り値は実行したか。
    // internal なのは、時刻に依存せず 1 周期だけを決定的に回して検証するためである。
    internal async Task<bool> TryRunCycleAsync(CancellationToken ct)
    {
        await using var lease = await leaseCoordinator.TryAcquireAsync(ct);
        if (lease is null)
        {
            logger.LogDebug(
                "ナレッジ健全性のリースを取得できなかった（他レプリカが実行中）。本周期をスキップする。");
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var collector = scope.ServiceProvider.GetRequiredService<KnowledgeHealthCollector>();
        await collector.RunAsync(ct);
        return true;
    }
}
