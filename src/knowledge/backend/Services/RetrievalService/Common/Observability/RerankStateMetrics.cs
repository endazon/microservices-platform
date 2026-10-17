using System.Diagnostics.Metrics;
using RetrievalService.Features.Search.Hybrid;

namespace RetrievalService.Common.Observability;

// NFR-01, NFR-29, ADR-0129 決定 2, [[IADR-0559]] (#1746 段 S5 の前提): **再順位付けの構成（`Rerank:Enabled`）をゲージで出す。**
//
// 警報は配備の構成に合う目標で鳴らす（ADR-0129 決定 2）。Prometheus のルールは構成を読めないので、
// サービスが自分の構成を `search_rerank_enabled`（1 = 有効・0 = 無効）として報告し、ルールがそれで閾値を選ぶ
// （`deploy/prometheus/alerts.yml` の `SearchLatencyP95High` と `SearchRerankLatencyTargetUndefined`）。
// 構成とルールを別々に書き換える形にしないのは、片方だけ直して食い違うのを避けるためである。
//
// 🔴 **起動時に作る**（`Program.cs` が Build の直後に解決する）。最初の検索まで系列が無いと、その間は
// 「無効」として扱われる（ルールはゲージが無いときに既定〔無効〕の 1.5 秒で鳴る側へ倒す）。
public sealed class RerankStateMetrics
{
    public const string MeterName = KeywordSearchMetrics.MeterName;

    // Prometheus 側の名前は `search_rerank_enabled`（単位の `{state}` は名前に付かない。`documents_content_abac_gate_open` と同じ写し方）。
    public const string EnabledGaugeName = "search.rerank.enabled";

    public RerankStateMetrics(IMeterFactory meterFactory, SearchRerankOptions options)
    {
        // 構成は起動時に読んだ値で固定である（`Program.cs` が段の登録を決めるのと同じ値）。
        var enabled = options.Enabled ? 1 : 0;
        meterFactory.Create(MeterName).CreateObservableGauge(
            EnabledGaugeName, () => enabled, unit: "{state}",
            description: "検索結果の再順位付けの構成。1 = 有効（Rerank:Enabled=true）・0 = 無効。検索 p95 の警報の閾値の選択に使う（ADR-0129 決定 2）");
    }
}
