using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationService.Common.Options;
using NotificationService.Features.Notifications.DispatchEmails;
using NotificationService.Features.Notifications.PurgeExpired;

namespace NotificationService.Features.Notifications;

// FR-22, IADR-0215 決定 2・4: outbox の送出と保持期限切れの掃除を定期的に回す。
//
// ★ **これは「発火の結線」ではない。** 発火（①②③）は FR-19 / FR-20 の機能が契機であり
// #451 の解除後に足す（IADR-0215 決定 5 の表がそのまま指示になる）。本サービスは
// **既に積まれた outbox を送り出す器**であり、発火源が無くても正しく何もしない。
//
// **テストでは無効にする**（Notification:MaintenanceEnabled=false）。器がホストを起こすたびに
// 背景で送出が走ると、時刻に依存した検証が不安定になる。
public sealed class NotificationMaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<NotificationMaintenanceHostedService> logger) : BackgroundService
{
    private readonly NotificationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.MaintenanceEnabled)
        {
            logger.LogInformation("通知の定期処理は無効です（Notification:MaintenanceEnabled=false）。");
            return;
        }

        using var timer = new PeriodicTimer(_options.MaintenanceInterval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var now = clock.GetUtcNow();

                var dispatcher = scope.ServiceProvider.GetRequiredService<EmailOutboxDispatcher>();
                await dispatcher.DispatchPendingAsync(now, stoppingToken);

                var retention = scope.ServiceProvider.GetRequiredService<NotificationRetention>();
                await retention.PurgeExpiredAsync(now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 1 周の失敗で常駐処理を止めない（止めると以後の送出が静かに滞留する）。
                logger.LogError(ex, "通知の定期処理で例外が発生しました。次の周期で再試行します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
