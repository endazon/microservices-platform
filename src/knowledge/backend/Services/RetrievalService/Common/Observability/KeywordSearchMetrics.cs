using System.Diagnostics.Metrics;

namespace RetrievalService.Common.Observability;

// FR-03, UC-01, SC-10, #1116, [[IADR-0318]] 決定 3: **ハイブリッド検索の全文（キーワード）側が
// 使えなかった回数**。
//
// 本 issue の欠陥は「壊れているのに 200 が返る」形そのものである。応答へ理由を載せることは
// できない（存在秘匿・[[IADR-0009]]／[[IADR-0313]] 決定 1 が案 3 を退けた）ので、
// **観測は応答の外側に置く**。#972 / #992 が「200 ＋ 空を緑にしない」を検証側で塞いだのと同じ向きである。
//
// **`EdgeTypeFallbackMetrics`（GraphService）と同型である。**
// - **0 が正常である。** 0 でない値そのものが検出になる。
// - **SC-10 の画面へは出さない**（「ナレッジ健全性」節は [[IADR-0119]] で節ごと着手保留）。
//   裁定が求めているのは「運用で観測できること」であり、Grafana で観測できれば成立する。
//
// 🔴 **クエリ文字列をタグにしない。** 利用者の入力は基数が無界であり、時系列 DB の系列数が爆発する。
// **理由はタグへ、詳細はログへ**分ける。理由の値域は 2 つに閉じている。
public sealed class KeywordSearchMetrics
{
    // Meter 名はサービス名と一致させる（OTLP の収集対象）。
    public const string MeterName = "microservices-platform.retrieval-service";
    public const string DegradedCounterName = "search.keyword_degraded.total";

    public const string ReasonTag = "search.keyword_degraded_reason";

    // 構成中のコレクションに `text` の全文ペイロードインデックスが無い（#1116 の欠陥そのもの）。
    // 🔴 **この理由は例外を伴わない。** Qdrant v1.18.1 は索引が無くても例外を投げず、
    // 部分文字列の全走査へ黙って落ちる（実測）。だからこそ readiness 側で「索引が在るか」を見る。
    public const string MissingIndexReason = "missing_index";

    // Qdrant が全文検索の要求を拒んだ（索引未作成で例外を返す旧版・接続断・不正なフィルタ等）。
    public const string BackendErrorReason = "backend_error";

    private readonly Counter<long> _degraded;

    public KeywordSearchMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _degraded = meter.CreateCounter<long>(
            DegradedCounterName,
            unit: "{query}",
            description: "ハイブリッド検索の全文（キーワード）側が使えず縮退した回数（0 が正常）");
    }

    public void RecordDegraded(string reason) =>
        _degraded.Add(1, new KeyValuePair<string, object?>(ReasonTag, reason));
}
