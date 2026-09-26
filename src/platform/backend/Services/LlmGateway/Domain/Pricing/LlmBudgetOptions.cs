namespace LlmGateway.Domain.Pricing;

// FR-11, NFR-21, ADR-0044, IADR-0466 (#1111): LLM 月次予算の上限（**用途別**）。
//
// 🔴 **既定値を持たない。** 計画（06_technical/05_observability-ops.md）は「月次予算の金額は定めない。
// 実測を待って確定する」と定めており、実装側で数字を置くとそれが既成事実として計画へ逆流する。
// 金額は所有者が設定する値であり（2026-09-26 の所有者裁定）、**未設定のあいだ上限アラートは
// 評価対象を持たず発火しない**（ゲージの系列が無い → ルールの式が空ベクタになる）。
//
// 🔴 **総額 1 本の上限は持たない。用途ごとである。** 用途には利用側プロジェクト（AST）の用途
// （trade-decision 等）が含まれ、計画は「基盤の費用と利用側プロジェクトの費用を合算して 1 つの数値に
// しない」と定めている（どちらの予算を超えたのか判別できなくなる）。
//
// 金額の通貨は単価表の通貨（`Llm:Pricing:Currency`）である。別の通貨欄は持たない ——
// 金額換算の結果（`llm.cost.total`）と同じ通貨で比べなければ意味が無いため。
public sealed class LlmBudgetOptions
{
    public const string SectionName = "Llm:Budget";

    // 用途（purpose）→ 直近 30 日の費用の上限（単価表の通貨）。キーは `Llm:Routing:PurposeModels` の
    // キーか `default`（LlmBudgetOptionsValidator が起動時に確かめる）。
    public Dictionary<string, decimal> MonthlyLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
