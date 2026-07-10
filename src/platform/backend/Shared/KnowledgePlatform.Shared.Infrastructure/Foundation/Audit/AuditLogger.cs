using Microsoft.Extensions.Logging;

namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Audit;

// FR-15, ADR-0004: 監査ログ。機微な取得・管理操作を構造化ログとして記録する（可観測性基盤へ集約）。
// 構成情報 API の取得操作（許可・拒否）を記録する用途で導入する。
public interface IAuditLogger
{
    // action: 操作種別（例 "config.read"）、subject: 実行主体（利用者名）、
    // outcome: 結果（"granted" / "denied"）、detail: 補足（任意）。
    void Record(string action, string subject, string outcome, string? detail = null);
}

public sealed class AuditLogger(ILogger<AuditLogger> logger) : IAuditLogger
{
    public void Record(string action, string subject, string outcome, string? detail = null)
    {
        // Audit=true を構造化プロパティに付与し、可観測性基盤（Serilog→OTLP）で監査として抽出可能にする。
        logger.LogInformation(
            "Audit: action={AuditAction} subject={AuditSubject} outcome={AuditOutcome} detail={AuditDetail} {Audit}",
            action, subject, outcome, detail ?? string.Empty, true);
    }
}
