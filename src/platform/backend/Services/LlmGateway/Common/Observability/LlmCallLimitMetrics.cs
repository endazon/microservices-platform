using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LlmGateway.Common.Observability;

// NFR-18, NFR-21, IADR-0678 決定 4 (#2404): 送る前の上限を超えた呼び出しの件数。
//
// 段 1（Report）では「止めていたら止まった件数」、段 2（Enforce）では「止めた件数」である。
// **段 2 へ切り替える前に、この系列（llm.limit.mode=report）で誤って止める呼び手が無いことを確かめる。**
//
// 属性の値域は閉じる（IADR-0110 の規律）: 種類は 6 値、段は 2 値、用途は計器と同じ正規化、
// 呼び手は検証済みのトークンのクライアント識別子（realm の有限集合。MachinePrincipal と同じ規律）。
public sealed class LlmCallLimitMetrics
{
    public const string MeterName = LlmCompletionMetrics.MeterName;
    public const string ExceededCounterName = "llm.call_limit.exceeded.total";

    public const string KindTag = "llm.limit.kind";
    public const string ModeTag = "llm.limit.mode";
    public const string CallerTag = "llm.caller";

    private readonly Counter<long> _exceeded;

    public LlmCallLimitMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _exceeded = meter.CreateCounter<long>(
            ExceededCounterName, unit: "{completion}",
            description: "送る前の上限（max_tokens・プロンプト長・呼び手と呼び手×用途の要求数と当日の費用）を超えた呼び出しの件数。"
                       + "llm.limit.mode=report は送った（段 1）、enforce は送らなかった（段 2）。");
    }

    public void RecordExceeded(string kind, string mode, string caller, string normalizedPurpose)
        => _exceeded.Add(1, new TagList
        {
            { KindTag, kind },
            { ModeTag, mode },
            { CallerTag, caller },
            { LlmCompletionMetrics.PurposeTag, normalizedPurpose },
        });
}
