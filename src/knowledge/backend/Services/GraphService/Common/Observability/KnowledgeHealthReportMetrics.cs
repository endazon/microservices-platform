using System.Diagnostics.Metrics;

namespace GraphService.Common.Observability;

// FR-10, NFR-21, SC-10, ADR-0006, ADR-0076 決定 3, [[IADR-0389]] 決定 5 (#1246):
// **ナレッジ健全性の観測値を受け口へ届けた回数**（指標ごと）。
//
// ## なぜ「件数」ではなく「届けた回数」を計器にするのか
//
// 指標そのもの（孤立文書が何件か）は受け口の DB が持っており、Prometheus へは出さない
// （IADR-0011: 業務指標は DashboardService、技術指標は可観測性スタック）。
// ここで測るのは**生産者が生きているか**である —— 収集が止まると受け口の数字は
// **最後に届いた値のまま凍る**（スナップショット置換なので 0 にすらならない）。
// 凍った数字は画面上で「安定している」に見え、**沈黙が正常と読める**。
//
// 🔴 **これが `absent` 系アラートの土台である**（ADR-0076 決定 3・[[IADR-0370]]）。
// 系列が消えたこと自体を鳴らすには、**平常時に必ず存在する系列**が要る。
//
// ## タグの基数は閉じている
//
// 指標名は受け口が値域を閉じた 7 語のうち本サービスが送る 4 語だけであり、無界にならない
// （`EdgeTypeFallbackMetrics` が型名をタグにしないのと同じ判断基準を、こちらは満たしている）。
public sealed class KnowledgeHealthReportMetrics
{
    // Meter 名はサービス名と一致させる（OTLP の収集対象）。`EdgeTypeFallbackMetrics` と同値。
    public const string MeterName = "microservices-platform.graph-service";

    // 🔴 Prometheus 側では `knowledge_health_report_total` になる（`.` → `_`）。
    // **アラート式（deploy/prometheus/alerts.yml）がこの名前を書いている。** 変えるなら両方。
    public const string ReportCounterName = "knowledge.health.report.total";

    // 🔴 同じく `knowledge_indicator` になる。アラート式のラベル名と同値。
    public const string IndicatorTag = "knowledge.indicator";

    private readonly Counter<long> _reports;

    public KnowledgeHealthReportMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _reports = meter.CreateCounter<long>(
            ReportCounterName,
            unit: "{report}",
            description: "ナレッジ健全性の観測値を受け口へ届けた回数（指標ごと。0 ではなく不在が異常）");
    }

    // 🔴 **受け口が受理したときだけ数える。** 送出は fail-open であり、
    // 到達できなくてもログを出して続行する。試みた回数を数えると、
    // **受け口が死んでいる間も系列が生き続け、`absent` が沈黙する。**
    public void RecordDelivered(string indicator) =>
        _reports.Add(1, new KeyValuePair<string, object?>(IndicatorTag, indicator));
}
