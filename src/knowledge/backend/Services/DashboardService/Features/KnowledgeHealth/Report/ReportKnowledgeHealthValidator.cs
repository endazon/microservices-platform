using DashboardService.Domain;
using FluentValidation;

namespace DashboardService.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-18, planning#494 決定 3, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0393: 観測値の受け口の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 2 本であった。
//
// 🔴 **振る舞いを変えない移送である。** 同じ 400・同じ本文を返すため、次の 2 点を守る:
//   1. **規則の宣言順を元のガード節の順に揃える**（Indicator → ThresholdDays）。
//      FluentValidation は既定で全規則を走らせるが、呼び出し側が `Errors[0]` を採ることで
//      元の「最初の違反で返す」と同じ文字列になる。順序を入れ替えると本文が変わる。
//   2. **メッセージは元の式のまま持ち上げる。** 指標の一覧は `KnowledgeHealthIndicators.All` から
//      組み立てる —— 指標を足したときにメッセージだけが古いまま残る形にしない。
internal sealed class ReportKnowledgeHealthValidator : AbstractValidator<KnowledgeHealthReportRequest>
{
    // 🔴 `const` にできない（`string.Join` は定数式にならない）。移送前と同じ文字列を
    // **同じ式から**作るために `static readonly` にする。
    internal static readonly string IndicatorInvalidMessage =
        "indicator must be one of: " + string.Join(", ", KnowledgeHealthIndicators.All);

    // planning#494 決定 3 (#1186): しきい値は省略可だが、**0 以下は受け付けない**。
    internal const string ThresholdInvalidMessage = "thresholdDays must be greater than zero";

    public ReportKnowledgeHealthValidator()
    {
        // FR-10, FR-17, FR-18: 指標名は既知の集合に限る。
        RuleFor(r => r.Indicator)
            .Must(KnowledgeHealthIndicators.IsValid)
            .WithMessage(IndicatorInvalidMessage);

        // planning#494 決定 3 (#1186): 省略（null）は許すが、0 以下は許さない ——
        // 保存すると画面が「しきい値 0 日」と表示し、件数の意味が読めなくなる。
        RuleFor(r => r.ThresholdDays)
            .Must(days => days is not <= 0)
            .WithMessage(ThresholdInvalidMessage);
    }
}
