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
    // NFR-18, IADR-0698 決定 3: 当日の費用の枠を置き場へ書く所要時間（判定器の錠の中で同期に走る）。p99 を見る。
    public const string StateSaveDurationName = "llm.call_limit.state_save.duration";
    public const string ResultTag = "llm.result";
    public const string ResultOk = "ok";
    public const string ResultError = "error";

    // 秒。fsync の所要時間（ミリ秒〜秒）を分けられる境界（既定の境界はミリ秒向けの値で秒の計器には粗すぎる）。
    private static readonly double[] StateSaveBuckets = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5];

    public const string KindTag = "llm.limit.kind";
    public const string ModeTag = "llm.limit.mode";
    public const string CallerTag = "llm.caller";

    private readonly Counter<long> _exceeded;
    private readonly Counter<long> _unpricedEstimate;
    private readonly Histogram<double> _stateSave;

    public LlmCallLimitMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _exceeded = meter.CreateCounter<long>(
            ExceededCounterName, unit: "{completion}",
            description: "送る前の上限（max_tokens・プロンプト長・呼び手と呼び手×用途の要求数と当日の費用）を超えた呼び出しの件数。"
                       + "cost_state は当日の費用の枠を置き場で読めない・書けない（費用を数えられない）。"
                       + "llm.limit.mode=report は送った（段 1）、enforce は送らなかった（段 2）。");
        _stateSave = meter.CreateHistogram(
            StateSaveDurationName, unit: "s",
            description: "当日の費用の枠を置き場へ書いた所要時間（llm.result=ok / error）。判定器の錠の中で同期に走るので、遅いと呼び出しが待つ。",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = StateSaveBuckets });
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

    // 結果は ok / error の 2 値（置き場の種類・パスは載せない）。
    public void RecordStateSave(double seconds, bool ok)
        => _stateSave.Record(seconds, new TagList { { ResultTag, ok ? ResultOk : ResultError } });

    public void RecordExceeded(string kind, string mode, string caller, string normalizedPurpose)
        => _exceeded.Add(1, new TagList
        {
            { KindTag, kind },
            { ModeTag, mode },
            { CallerTag, caller },
            { LlmCompletionMetrics.PurposeTag, normalizedPurpose },
        });
}
