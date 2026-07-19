using DataSourceService.Api.Foundation.Domain;
using DataSourceService.Api.Foundation.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataSourceService.Api.Foundation.Services;

// FR-01, UC-04（基本フロー: システムが定期的に原本を取得）, IADR-0051: 定期同期ワーカー。
// 既定は無効（DataSourceSync:Enabled=false）。有効時は一定間隔で active データソースをコネクタ経由で同期する。
// scoped な DataSourceSyncService/DbContext を各サイクルでスコープ解決する（singleton→scoped の橋渡し）。
// IADR-0083 (#305): 本番マルチレプリカでの冗長 fetch を排除するため、各サイクルは排他リース（advisory lock）を
// 取得したレプリカのみが実行する（単一書き手化）。単一レプリカ（経路B）は常に取得でき従来どおり動く。
public sealed class DataSourceSyncHostedService(
    IServiceScopeFactory scopeFactory,
    ISyncLeaseCoordinator leaseCoordinator,
    IOptions<DataSourceSyncOptions> options,
    ILogger<DataSourceSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        if (!opt.Enabled)
        {
            logger.LogInformation("定期同期は無効です（DataSourceSync:Enabled=false）。手動 /sync のみ有効。");
            return;
        }

        // 過負荷防止のため最短 30 秒に丸める。
        var interval = TimeSpan.FromSeconds(Math.Max(30, opt.IntervalSeconds));
        logger.LogInformation("定期同期を開始します（間隔 {Seconds} 秒）", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await TryRunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
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
