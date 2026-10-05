using System.Diagnostics.Metrics;

namespace RetrievalService.Common.Observability;

// FR-03, FR-10, SC-02, ADR-0127 決定 3, [[IADR-0498]] 決定 7 (#1746 段 S2): **再順位付けの段の結果を数える。**
//
// 🔴 **縮退（元の順へ戻した）を黙らせない。** 段は失敗しても元の順で 200 を返す（fail-open は並びについてだけ）ため、
// 応答からは「再順位付けが効いていない」ことが読めない。`KeywordSearchMetrics` と同じく**観測は応答の外側に置く**。
// **費用（トークン・金額）はここで数えない** —— 単価を解決して積むのはゲートウェイ（ADR-0044 決定 3。用途 `rerank`）であり、
// ここで数えると換算の正が 2 か所に割れる。ここが数えるのは「掛けたか・掛けなかったか・なぜか」だけである。
//
// 🔴 **クエリ文字列・文書 ID・利用者をタグにしない**（基数が無界・利用者の行動の記録に踏み込む）。値域は下の定数に閉じる。
public sealed class RerankMetrics
{
    public const string MeterName = KeywordSearchMetrics.MeterName;
    public const string CounterName = "search.rerank.total";

    public const string ResultTag = "search.rerank_result";
    public const string ReasonTag = "search.rerank_reason";

    // 結果（3 値）。
    public const string Applied = "applied";
    public const string Degraded = "degraded";
    public const string Skipped = "skipped";

    // 理由（閉じた値域）。`applied` の理由は `none`。
    public const string None = "none";
    // skipped: 設計どおり掛けない。
    public const string SortUpdated = "sort_updated";
    public const string SemanticMode = "semantic";
    public const string Synthetic = "synthetic";
    public const string TooFewCandidates = "too_few";
    // degraded: 掛けようとして元の順へ戻した。
    public const string Timeout = "timeout";
    public const string Transport = "transport";
    public const string NotSent = "not_sent";
    public const string Refusal = "refusal";
    public const string Unparseable = "unparseable";

    private readonly Counter<long> _counter;

    public RerankMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _counter = meter.CreateCounter<long>(
            CounterName,
            unit: "{search}",
            description: "検索結果の再順位付けの段の結果（applied / degraded / skipped と理由）。degraded は元の順で返した回数");
    }

    public void Record(string result, string reason) =>
        _counter.Add(1,
            new KeyValuePair<string, object?>(ResultTag, result),
            new KeyValuePair<string, object?>(ReasonTag, reason));
}
