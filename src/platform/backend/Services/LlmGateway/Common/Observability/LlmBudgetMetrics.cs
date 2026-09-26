using System.Diagnostics.Metrics;
using LlmGateway.Domain.Pricing;
using Microsoft.Extensions.Options;

namespace LlmGateway.Common.Observability;

// FR-11, NFR-21, ADR-0044, IADR-0466 決定 3 (#1111): 月次予算の上限を**ゲージとして出す**。
//
// 上限アラート（deploy/prometheus/alerts.yml の LlmMonthlyBudgetExceeded）は、直近 30 日の
// `llm_cost_total` をこのゲージ `llm_budget_monthly_limit` と用途・通貨で突き合わせる。
// **金額はルールの式に書かない** —— 式に数字を書くと、金額の置き場がゲートウェイの設定と
// ルールファイル（4 か所）に割れ、「どこを直せば上限が変わるか」が 1 つに決まらない。
//
// 🔴 **設定された用途だけを出す。未設定なら 1 件も出さない。** 系列が無ければルールは空ベクタになり
// 発火しない（不活性）。`absent()` 系の補助ルールは置かない —— 金額が未設定のあいだ恒常発火し、
// 「既知の誤報」を作る（#1111 で却下済み。IADR-0322 決定 3）。
//
// Meter は補完カウンタ・利用実績と同じもの（Program.cs の AddMeter は 1 つで足りる）。
public sealed class LlmBudgetMetrics
{
    public const string MeterName = LlmCompletionMetrics.MeterName;

    // Prometheus 側の名前は `llm_budget_monthly_limit`（`{currency}` は注記の単位で接尾辞にならない。
    // 導出の根拠は IADR-0466 §Prometheus 側の名前）。scripts.repo.test.js がルールの式と突き合わせる。
    public const string LimitGaugeName = "llm.budget.monthly_limit";

    private readonly IOptionsMonitor<LlmBudgetOptions> _budget;
    private readonly ModelPriceTable _prices;

    public LlmBudgetMetrics(IMeterFactory meterFactory, IOptionsMonitor<LlmBudgetOptions> budget, ModelPriceTable prices)
    {
        _budget = budget;
        _prices = prices;
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge(
            LimitGaugeName, Observe, unit: "{currency}",
            description: "用途別の月次予算の上限（単価表の通貨）。所有者が Llm:Budget:MonthlyLimits に設定した用途だけが "
                       + "系列を持つ。**系列が無い用途は上限アラートの評価対象にならない**（金額は実装側で定めない）。");
    }

    // 収集のたびに現在の設定を読む。タグ値は llm.cost.total と同じ軸（用途は小文字・通貨は単価表の通貨）。
    private IEnumerable<Measurement<double>> Observe()
    {
        var currency = _prices.Currency;
        foreach (var (purpose, limit) in _budget.CurrentValue.MonthlyLimits)
        {
            yield return new Measurement<double>((double)limit,
                new KeyValuePair<string, object?>(LlmCompletionMetrics.PurposeTag, purpose.ToLowerInvariant()),
                new KeyValuePair<string, object?>(LlmUsageMetrics.CurrencyTag, currency));
        }
    }
}
