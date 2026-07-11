using Platform.Shared.Contracts.Dtos;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, IADR-0029 フォローアップ 4 (#145): 1 回のドリフト検出（実効収集＋宣言突合＋不一致警告）。
// 定期検出（DriftDetectionHostedService）と、適用直後の即時検出（ArgoCD PostSync フックからの起動）の
// 双方で共有する単一の実行経路。不一致は IDriftAlertSink（既定は構造化ログ ConfigDrift=true）へ通知する。
public interface IDriftRunner
{
    Task<DriftReportDto> RunOnceAsync(CancellationToken ct = default);
}

public sealed class DriftRunner(
    IConfigInspectionService inspection,
    IDriftAlertSink alertSink,
    ILogger<DriftRunner> logger) : IDriftRunner
{
    public async Task<DriftReportDto> RunOnceAsync(CancellationToken ct = default)
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
        return report;
    }
}
