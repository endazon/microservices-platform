using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Audit;

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
        // Audit=true を構造化プロパティに付与し、可観測性基盤（ILogger→OTel Logging SDK→OTLP。IADR-0216）で
        // 監査として抽出可能にする。プロパティが LogRecord の属性になるのは ParseStateValues = true による。
        //
        // 🔴 CodeQL(cs/log-forging) アラート #19 (#1019): **4 引数すべてが呼び出し側由来**である。
        // `subject` は利用者名（トークンのクレーム）、`detail` は自由文で、**値域が閉じていない**。
        // 未加工で行指向のログへ落とすと、改行を仕込むだけで偽の監査行を注入できる
        // （CWE-117）。**監査ログでこれが起きると、偽造行と本物の区別が付かなくなる。**
        //
        // `action` / `outcome` は現状すべて呼び出し側のリテラルだが、**同じ 1 行に載る以上
        // 同じ扱いにする** —— 4 つのうち 2 つだけ通す形にすると、後から引数を足した人が
        // 「どれを通すのか」を復元できない。
        logger.LogInformation(
            "Audit: action={AuditAction} subject={AuditSubject} outcome={AuditOutcome} detail={AuditDetail} {Audit}",
            LogSanitizer.Sanitize(action),
            LogSanitizer.Sanitize(subject),
            LogSanitizer.Sanitize(outcome),
            LogSanitizer.Sanitize(detail),
            true);
    }
}
