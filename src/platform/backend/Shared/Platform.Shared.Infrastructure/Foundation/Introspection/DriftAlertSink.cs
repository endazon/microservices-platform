using Platform.Shared.Contracts.Dtos;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15: ドリフト検出時の警告通知先。既定は構造化ログ（Warning）へ出力し、可観測性基盤
// （05_observability-ops.md）のアラート経路へ流す。テストではダブルで検証する。
public interface IDriftAlertSink
{
    Task AlertAsync(DriftReportDto report, CancellationToken ct = default);
}

public sealed class LoggingDriftAlertSink(ILogger<LoggingDriftAlertSink> logger) : IDriftAlertSink
{
    public Task AlertAsync(DriftReportDto report, CancellationToken ct = default)
    {
        foreach (var f in report.Findings)
        {
            // ConfigDrift=true をキーに運用アラート（05_observability-ops）で抽出・通知する。
            logger.LogWarning(
                "Configuration drift detected: kind={DriftKind} severity={DriftSeverity} target={DriftTarget} detail={DriftDetail} {ConfigDrift}",
                f.Kind, f.Severity, f.Target, f.Detail, true);
        }
        return Task.CompletedTask;
    }
}
