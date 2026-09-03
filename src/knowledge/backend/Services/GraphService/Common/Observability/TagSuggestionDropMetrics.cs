using System.Diagnostics.Metrics;

namespace GraphService.Common.Observability;

// FR-18, SC-09, SC-10, ADR-0063 決定 2, IADR-0361 決定 2 (#1014): **生成段で辞書と突き合わせて
// 落としたタグ提案の件数**。
//
// `EdgeTypeFallbackMetrics`（#912）と同型である。同じ形を採る理由:
// - **0 が正常である。** 辞書を LLM へ渡している以上、辞書外の値が返ること自体が規定外の事象であり、
//   0 でない値が「LLM が値集合を守っていない」または「辞書が引けていない」の検出になる。
// - **SC-10 の画面へは出さない。** Grafana で観測できれば足りる（辺の型のフォールバックと同じ判断）。
//
// 🔴 **タグ値をタグにもログにも出さない。** LLM が返す自由文であり基数が無界（時系列 DB の系列数が
// 爆発する）なうえ、本文由来の語を含み得る。**件数だけを数える。**
public sealed class TagSuggestionDropMetrics
{
    public const string MeterName = EdgeTypeFallbackMetrics.MeterName;
    public const string DroppedCounterName = "graph.tag_suggestion_dropped.total";

    // なぜ落としたか。**値域は 2 つに閉じている。**
    //   out_of_dictionary      … 辞書は引けたが、その値が無い
    //   dictionary_unavailable … 辞書が引けず、fail-closed で全件落とした
    public const string ReasonTag = "graph.drop_reason";
    public const string OutOfDictionary = "out_of_dictionary";
    public const string DictionaryUnavailable = "dictionary_unavailable";

    private readonly Counter<long> _dropped;

    public TagSuggestionDropMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _dropped = meter.CreateCounter<long>(
            DroppedCounterName,
            unit: "{suggestion}",
            description: "生成段でタグ辞書に無いため落としたタグ提案の件数（0 が正常）");
    }

    public void RecordDropped(string reason, long count = 1)
    {
        if (count <= 0) return;
        _dropped.Add(count, new KeyValuePair<string, object?>(ReasonTag, reason));
    }
}
