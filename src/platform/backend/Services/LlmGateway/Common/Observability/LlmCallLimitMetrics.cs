using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LlmGateway.Common.Observability;

// NFR-18, NFR-21, IADR-0678 決定 4 (#2404): 送る前の上限を超えた呼び出しの件数。
//
// 段 1（Report）では「止めていたら止まった件数」、段 2（Enforce）では「止めた件数」である。
// **段 2 へ切り替える前に、この系列（llm.limit.mode=report）で誤って止める呼び手が無いことを確かめる。**
//
// 属性の値域は閉じる（IADR-0110 の規律）: 種類は 7 値（cost_state は IADR-0698）、段は 2 値、用途は計器と同じ正規化、
// 呼び手は検証済みのトークンのクライアント識別子（realm の有限集合。MachinePrincipal と同じ規律）。
public sealed class LlmCallLimitMetrics
{
    public const string MeterName = LlmCompletionMetrics.MeterName;
    public const string ExceededCounterName = "llm.call_limit.exceeded.total";
    public const string UnpricedEstimateCounterName = "llm.call_limit.unpriced_estimate.total";

    public const string KindTag = "llm.limit.kind";
    public const string ModeTag = "llm.limit.mode";
    public const string CallerTag = "llm.caller";

    private readonly Counter<long> _exceeded;
    private readonly Counter<long> _unpricedEstimate;

    public LlmCallLimitMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _exceeded = meter.CreateCounter<long>(
            ExceededCounterName, unit: "{completion}",
            description: "送る前の上限（max_tokens・プロンプト長・呼び手と呼び手×用途の要求数と当日の費用）を超えた呼び出しの件数。"
                       + "cost_state は当日の費用の枠を置き場で読めない・書けない（費用を数えられない）。"
                       + "llm.limit.mode=report は送った（段 1）、enforce は送らなかった（段 2）。");
        _unpricedEstimate = meter.CreateCounter<long>(
            UnpricedEstimateCounterName, unit: "{completion}",
            description: "費用の上限の判定で、単価表に無いモデルを『表に無いモデル用の計上単価』で見積もった件数（0 が正常。単価表の登録漏れの検出）。");
    }

    public void RecordUnpricedEstimate(string caller, string normalizedPurpose)
        => _unpricedEstimate.Add(1, new TagList
        {
            { CallerTag, caller },
            { LlmCompletionMetrics.PurposeTag, normalizedPurpose },
        });

    public void RecordExceeded(string kind, string mode, string caller, string normalizedPurpose)
        => _exceeded.Add(1, new TagList
        {
            { KindTag, kind },
            { ModeTag, mode },
            { CallerTag, caller },
            { LlmCompletionMetrics.PurposeTag, normalizedPurpose },
        });
}
