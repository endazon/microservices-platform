using Microsoft.Extensions.Options;

namespace DashboardService.Features.Dashboard.PurgeExpired;

// FR-10, UC-05, SC-10, ADR-0072 決定 3, [[IADR-0367]] (#1198):
// 保持期間を過ぎた利用イベントの削除を定期的に回す常駐処理。
//
// 形は `NotificationMaintenanceHostedService`（platform 側の前例。`BackgroundService` ＋
// `PeriodicTimer` ＋ `IServiceScopeFactory`）をなぞる。**1 周の失敗で常駐処理を止めない** ——
// 止めると保持期間の統制が静かに効かなくなり、**画面も応答も何も変わらないまま行だけが溜まる。**
//
// ★ **初回は起動直後に回す**（`do { } while (WaitForNextTickAsync)`）。1 周期後にすると、
// 再起動の多い環境では**一度も回らないまま次の再起動を迎える**。保持期間は 90 日であり、
// 起動直後の 1 周が重い環境でも `BatchSize` で区切られている。
public sealed class UsageRetentionHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<UsageRetentionOptions> options,
    ILogger<UsageRetentionHostedService> logger) : BackgroundService
{
    private readonly UsageRetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation(
                "利用イベントの保持期間の削除は無効である（{Key}:Enabled=false）。"
                + "**行は無期限に残る** —— 意図した構成であることを確かめること。",
                UsageRetentionOptions.SectionName);
            return;
        }

        // 🔴 **報告する値は倒した後の値である**（[[IADR-0357]] / [[IADR-0353]] の作法）。
        // 構成値をそのまま出すと、ログの周期と実際の周期が食い違う。
        if (_options.HasInvalidInterval)
            logger.LogWarning(
                "利用イベントの掃除間隔の構成が不正である（{Configured} 分）。既定の {Default} 分へ倒した。"
                + "構成キーは {Key}:IntervalMinutes である。",
                _options.IntervalMinutes, UsageRetentionOptions.DefaultIntervalMinutes,
                UsageRetentionOptions.SectionName);

        var interval = _options.EffectiveInterval;
        logger.LogInformation(
            "利用イベントの保持期間の削除を開始する（保持 {RetentionDays} 日・間隔 {Interval} 分）。",
            UsageRetentionOptions.RetentionDays, (int)interval.TotalMinutes);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 1 周の失敗で常駐処理を止めない（止めると以後の削除が静かに滞留する）。
                logger.LogError(ex, "利用イベントの保持期間の削除で例外が発生した。次の周期で再試行する。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // 1 周ぶんの掃除。`BatchSize` で区切られているため、**上限に達したら同じ周で続ける**
    // （残りを次の周期まで放置すると、初回適用時に何日も消え切らない）。
    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        var total = 0;
        int deleted;
        do
        {
            using var scope = scopeFactory.CreateScope();
            var retention = scope.ServiceProvider.GetRequiredService<UsageEventRetention>();
            deleted = await retention.PurgeExpiredAsync(stoppingToken);
            total += deleted;
        }
        while (deleted == UsageEventRetention.BatchSize && !stoppingToken.IsCancellationRequested);

        if (total > 0)
            logger.LogInformation(
                "保持期間（{RetentionDays} 日）を過ぎた利用イベントを {Count} 件削除した。",
                UsageRetentionOptions.RetentionDays, total);
    }
}
