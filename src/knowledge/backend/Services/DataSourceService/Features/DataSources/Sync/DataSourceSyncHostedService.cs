using DataSourceService.Domain.Ports;
using DataSourceService.Domain;
using DataSourceService.Features.DataSources;
using DataSourceService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataSourceService.Features.DataSources.Sync;

// FR-01, UC-04（基本フロー: システムが定期的に原本を取得）, IADR-0051: 定期同期ワーカー。
// 既定は無効（DataSourceSync:Enabled=false）。有効時は一定間隔で active データソースをコネクタ経由で同期する。
// scoped な DataSourceSyncService/DbContext を各サイクルでスコープ解決する（singleton→scoped の橋渡し）。
// IADR-0083 (#305): 本番マルチレプリカでの冗長 fetch を排除するため、各サイクルは排他リース（advisory lock）を
// 取得したレプリカのみが実行する（単一書き手化）。単一レプリカ（経路B）は常に取得でき従来どおり動く。
public sealed class DataSourceSyncHostedService(
    IServiceScopeFactory scopeFactory,
    ISyncLeaseCoordinator leaseCoordinator,
    SyncSchedule schedule,
    IOptions<DataSourceSyncOptions> options,
    ILogger<DataSourceSyncHostedService> logger) : BackgroundService
{
    // #1604: 周期の実際の長さ。**試験だけが与える**（構成の周期は最短 30 秒に丸められ、試験で待てない）。
    // null（本番）なら `StartSchedule()` が解決した実効間隔で刻む。SC-06 の「次回同期」の位相は常に構成の間隔で記録する。
    // 形は #1598 の `CycleInterval`（PrivateNoteMaintenance・GraphService の 3 つ）と同じ。
    internal TimeSpan? CycleInterval { get; init; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = StartSchedule();
        if (interval is null) return;

        using var timer = new PeriodicTimer(CycleInterval ?? interval.Value);
        try
        {
            do
            {
                try
                {
                    await TryRunCycleAsync(stoppingToken);
                }
                // ［2026-09-26 / #1604・IADR-0083 追記］🔴 **ループを抜けるのは停止要求（stoppingToken）の取り消しだけである。**
                // 従前は型だけの `catch (OperationCanceledException) { break; }` で、コネクタの接続の時間切れ
                // （HttpClient の TaskCanceledException）や Npgsql の取り消しが 1 度でも届くと、ログも残さず定期同期が**永久に**止まった
                // （プロセスは健全なまま）。停止要求の無い取り消しは下の捕捉で周期の失敗として記録し、次の周期へ進む。
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 1 サイクルの失敗で停止させない（次サイクルで回復）。
                    logger.LogError(ex, "定期同期サイクルでエラーが発生しました");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        // #1604: 拍を待つ間の停止要求はシャットダウンとして静かに終える（想定外の取り消しは例外のまま出す）。
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // シャットダウン。
        }
    }

    // 起動時の 1 回だけ行う設定解決。有効なら実効間隔を返し、同時に SC-06「次回同期」の起点を記録する。
    // 無効なら null を返す（＝ワーカーは回らない・次回実行時刻も無い）。
    // ExecuteAsync から切り出しているのは、時計を注入して決定的に検証できるようにするためである
    // （TryRunCycleAsync を internal にしているのと同じ理由）。
    internal TimeSpan? StartSchedule()
    {
        var opt = options.Value;
        if (!opt.Enabled)
        {
            logger.LogInformation("定期同期は無効です（DataSourceSync:Enabled=false）。手動 /sync のみ有効。");
            return null;
        }

        // 過負荷防止のため最短 30 秒に丸める。
        var interval = TimeSpan.FromSeconds(Math.Max(30, opt.IntervalSeconds));
        logger.LogInformation("定期同期を開始します（間隔 {Seconds} 秒）", interval.TotalSeconds);

        // SC-06（planning#200 Q15）, IADR-0136: PeriodicTimer は本メソッドの直後にこの間隔で刻み始める。
        // その位相を記録し、/datasources が「次に取り込まれるのはいつか」に答えられるようにする。
        schedule.Start(interval);
        return interval;
    }

    // IADR-0083 (#305): 単一書き手化のゲート。排他リースを取得できたレプリカのみが同期を実行する。
    // 取得できない（他レプリカが実行中／一時障害）場合は本サイクルをスキップし次周期へ（fail-safe）。
    // 戻り値は同期を実行したか（true=実行・false=スキップ）。テストからの検証に用いる。
    internal async Task<bool> TryRunCycleAsync(CancellationToken ct)
    {
        await using var lease = await leaseCoordinator.TryAcquireAsync(ct);
        if (lease is null)
        {
            logger.LogDebug("定期同期リースを取得できませんでした（他レプリカが実行中）。本サイクルをスキップします。");
            return false;
        }

        await SyncAllActiveAsync(ct);
        return true;
    }

    private async Task SyncAllActiveAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        var sync = scope.ServiceProvider.GetRequiredService<DataSourceSyncService>();

        var active = await db.DataSources
            .Where(d => d.Status == DataSourceStatus.Active)
            .ToListAsync(ct);

        foreach (var ds in active)
        {
            ct.ThrowIfCancellationRequested();
            // watermark（LastSyncedAt）前進は SyncAsync が完全成功時のみ実施する（失敗時は進めず次回再試行）。
            await sync.SyncAsync(ds, ct);
        }

        await db.SaveChangesAsync(ct);
    }
}
