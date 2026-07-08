using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, ADR-0018: 宣言と実効構成のドリフトを定期（既定 5 分）に検出し、不一致を運用アラートへ警告する。
// 起動直後に 1 回即時検出する（do-while の初回）。#146 で BFF は宣言（pipeline.json）変更時にロールアウト
// するため、宣言の適用直後もこの起動時検出で捕捉される。任意のタイミングの適用直後検出は ArgoCD PostSync
// フックからの POST /internal/config/drift-run（IDriftRunner 共有）で補完する（#145）。
public sealed class DriftDetectionHostedService(
    IDriftRunner runner,
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
            await SafeRunOnceAsync(stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    // 定期ループでは 1 回の検出失敗でループを止めないよう例外を握る（即時検出の即応性を保つ）。
    private async Task SafeRunOnceAsync(CancellationToken ct)
    {
        try
        {
            await runner.RunOnceAsync(ct);
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
