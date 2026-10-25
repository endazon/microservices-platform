using System.Diagnostics.Metrics;

namespace RetrievalService.Common.Observability;

// FR-03, NFR-06, ADR-0016, [[IADR-0534]] 決定 4 (#1871): **応答へ載せた縮退の印を、同じ符号で数える。**
//
// 埋め込みの縮退（クエリ埋め込みが空ベクトル）には従来、警告ログしか無かった（`WarnEmbeddingUnavailable`）。
// PoC では Voyage の鍵が無いあいだ全検索が語彙検索へ縮退していたが、ログを読みに行かない限り見えなかった。
// **応答の印は呼び出し元にしか見えない**ので、運用（Grafana）が同じ事実を読めるよう計器を置く。
//
// - **0 が正常である**（`KeywordSearchMetrics` と同型）。
// - 🔴 **タグの値域は応答の `degradedReasons` と同一**（`SearchDegradedReasons.All`）。別の語彙を作らない ——
//   呼び出し元が見た符号で、そのままダッシュボードを引けるようにする。
// - 1 回の検索で理由が 2 つあれば 2 つ数える（理由ごとの系列）。
// - 🔴 **クエリ文字列・利用者・コレクション名をタグにしない**（基数が無界・利用者の行動の記録に踏み込む）。
// - 既存の `search.rerank.total`（細かな理由つき）と `search.keyword_degraded.total` は残す。本計器は応答の印と対で読む集計である。
public sealed class SearchDegradationMetrics
{
    public const string MeterName = KeywordSearchMetrics.MeterName;
    public const string CounterName = "search.degraded.total";
    public const string ReasonTag = "search.degraded_reason";

    private readonly Counter<long> _counter;

    public SearchDegradationMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _counter = meter.CreateCounter<long>(
            CounterName,
            unit: "{search}",
            description: "検索が部品の縮退（埋め込み・グラフ展開・再順位付け）を伴って返った回数。理由ごと（0 が正常）");
    }

    public void Record(IEnumerable<string> reasons)
    {
        foreach (var reason in reasons)
            _counter.Add(1, new KeyValuePair<string, object?>(ReasonTag, reason));
    }
}
