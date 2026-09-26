using LlmGateway.Domain.Routing;
using Microsoft.Extensions.Options;

namespace LlmGateway.Domain.Pricing;

// FR-11, NFR-21, ADR-0044, IADR-0466 決定 2 (#1111): 月次予算の上限を起動時に検証する（ValidateOnStart）。
//
// **誤った設定を実行時に黙って読み飛ばさない。** 例えば用途名の綴り違いを許すと、ゲージの系列は
// 出るのに `llm_cost_total` 側に同じ `llm_purpose` が現れず、**式が空ベクタのまま「上限を設定した」と
// 読める**（#1110 の「評価されている ≠ 評価対象がある」と同じ形）。配備の失敗として表に出す
// （ModelPricingOptionsValidator と同じ判断）。
//
// **未設定（空）は成功である。** 金額を定めないことが計画の現在の状態であり、それは誤りではない。
public sealed class LlmBudgetOptionsValidator(IOptionsMonitor<LlmRoutingOptions> routing)
    : IValidateOptions<LlmBudgetOptions>
{
    // 用途が未指定の呼び出しに補う値（LlmMetricValues.DefaultPurpose と同値）。設定に依存せず常に既知。
    private const string DefaultPurpose = "default";

    public ValidateOptionsResult Validate(string? name, LlmBudgetOptions options)
    {
        var errors = new List<string>();
        var purposes = routing.CurrentValue.PurposeModels;

        foreach (var (purpose, limit) in options.MonthlyLimits)
        {
            // 🔴 `other`（未知の用途の集約先）は受け付けない。どの呼び出し元の予算かが決まらない値である。
            var known = string.Equals(purpose, DefaultPurpose, StringComparison.OrdinalIgnoreCase)
                     || purposes.ContainsKey(purpose);
            if (!known)
            {
                errors.Add($"Llm:Budget:MonthlyLimits:{purpose} は既知の用途ではありません。"
                    + $"Llm:Routing:PurposeModels のキーか {DefaultPurpose} を指定してください"
                    + $"（既知: {string.Join(", ", purposes.Keys.Append(DefaultPurpose).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))}）。");
            }

            // 0 以下の上限は「常に超過」か意味を持たない値であり、恒常発火を作る（既知の誤報）。
            if (limit <= 0m)
                errors.Add($"Llm:Budget:MonthlyLimits:{purpose} の金額が 0 以下です（{limit}）。正の値を指定するか、項目ごと削除してください。");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
