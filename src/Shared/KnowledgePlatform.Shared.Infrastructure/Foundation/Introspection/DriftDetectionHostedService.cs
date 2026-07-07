using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, ADR-0018: 宣言と実効構成のドリフトを定期（既定 5 分）に検出し、不一致を運用アラートへ警告する。
// 適用直後の検出はデプロイ側（ArgoCD 適用直後の呼び出し／構成情報 API の /drift 取得）で補完する。
public sealed class DriftDetectionHostedService(
    IConfigInspectionService inspection,
    IDriftAlertSink alertSink,
    IOptions<DriftDetectionOptions> options,
    ILogger<DriftDetectionHostedService> logger) : BackgroundService
{
    private readonly DriftDetectionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Drift detection is disabled by configuration");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.IntervalSeconds));
        logger.LogInformation("Drift detection started; interval={Interval}", interval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    // 1 回のドリフト検出を実行し、不一致があれば警告する。適用直後の即時検出にも再利用できる。
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var report = await inspection.GetDriftAsync(ct);
            if (report.HasDrift)
            {
                await alertSink.AlertAsync(report, ct);
            }
            else
            {
                logger.LogDebug("Drift detection: no drift ({CheckedAt})", report.CheckedAt);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Drift detection run failed");
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
