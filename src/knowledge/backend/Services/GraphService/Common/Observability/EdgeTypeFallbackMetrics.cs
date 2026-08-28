using System.Diagnostics.Metrics;

namespace GraphService.Common.Observability;

// FR-17, SC-10, ADR-0033 決定 3 (#912): **リンク抽出で未定義の辺の型が現れ、`related` へ
// フォールバックした件数**。
//
// ADR-0033 決定 3 は「未定義の型が来た場合は `related` へフォールバックし、**警告を記録して**
// 取り込む」「フォールバックの発生件数は SC-10（運用ダッシュボード）で観測できるようにする」と
// 定めている。**保持先の決定は #910 の仕様書が本 issue（#912）へ送っていたもの**であり、
// 抽出側の OTel カウンタとして持つことに決めた（IADR-0281）。
//
// **`IngestTagMetrics` と同型である**（DocumentService。#637 / planning#304）。同じ形を採る理由:
// - **0 が正常である。** 辞書に無い型が現れること自体が規定外の事象であり、0 でない値が検出になる。
// - **SC-10 の画面へは出さない。** 「ナレッジ健全性」節は [[IADR-0119]] により節ごと着手保留であり、
//   ここに 1 指標だけ差し込むと保留の線引きが壊れる。**裁定が求めたのは「観測できること」であり、
//   Grafana で観測できれば成立する。** 画面の行は保留が解けて同節を作るときに一緒に置く。
//
// 🔴 **型名をタグにしない。** 未定義の型名は書き手が自由に書けるフロントマターのキー名であり、
// 基数が無界である。時系列 DB の系列数が爆発するので、**型名はログへ、件数はカウンタへ**分ける。
public sealed class EdgeTypeFallbackMetrics
{
    // Meter 名はサービス名と一致させる（OTLP の収集対象）。
    public const string MeterName = "microservices-platform.graph-service";
    public const string FallbackCounterName = "graph.edge_type_fallback.total";

    // どの層の既定が落ちたか（`explicit` = フロントマターの明示指定 / `contextual` = 構文の文脈既定）。
    // **値域は 2 つに閉じている**（3 層のうち ③ は既定型そのものなのでフォールバックしない）。
    public const string LayerTag = "graph.fallback_layer";
    public const string ExplicitLayer = "explicit";
    public const string ContextualLayer = "contextual";

    private readonly Counter<long> _fallbacks;

    public EdgeTypeFallbackMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _fallbacks = meter.CreateCounter<long>(
            FallbackCounterName,
            unit: "{link}",
            description: "リンク抽出で未定義の辺の型が現れ related へフォールバックした件数（0 が正常）");
    }

    public void RecordFallback(string layer) =>
        _fallbacks.Add(1, new KeyValuePair<string, object?>(LayerTag, layer));
}
