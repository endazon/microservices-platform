using System.Diagnostics.Metrics;

namespace GraphService.Common.Observability;

// FR-17, SC-10, ADR-0035 決定 3, ADR-0076 決定 3, [[IADR-0496]] 決定 5 (#1733):
// **日次のクラスタ検出が最後に成功した時刻**（Unix 秒のゲージ）。
//
// ## なぜ要るか
//
// 検出が止まっても、クラスタの行は**最後の検出のまま残る**。SC-18 の表示も SC-10 の未要約クラスタ数も
// 古い値のまま正常に見え、**沈黙が正常と読める**（#1733 は 9/27 から 1 週間、誰も気付かなかった）。
// 遅れは `time() - graph_cluster_detection_last_success_timestamp_seconds` で読む。
//
// ## 系列を出さない間がある
//
// 🔴 **記録をまだ読めていない・記録が無いときは観測値を出さない。** 0 を出すと「1970 年に成功した」になり、
// 遅れの式が 56 年を返す（鳴らす理由としては正しいが、記録が無いことと区別できない）。
// 不在は `absent()` で別に読める。値は `graph_batch_runs` の行であり、**どのレプリカが読んでも同じ**である
// （リースを取れなかったレプリカも、次に判定したときに読み直して追いつく）。
public sealed class ClusterDetectionMetrics
{
    // Meter 名は `EdgeTypeFallbackMetrics` と同値（Program.cs の AddMeter を増やさない）。
    public const string MeterName = EdgeTypeFallbackMetrics.MeterName;

    // 🔴 Prometheus 側では `graph_cluster_detection_last_success_timestamp_seconds` になる（`.` → `_`・単位 s → `_seconds`）。
    public const string LastSuccessGaugeName = "graph.cluster_detection.last_success.timestamp";

    // 「未観測」の印。0 は正当な値（1970-01-01）でもあるので使わない。
    private const long Unknown = long.MinValue;

    private long _lastSuccessUnixSeconds = Unknown;

    public ClusterDetectionMetrics(IMeterFactory meterFactory)
    {
        meterFactory.Create(MeterName).CreateObservableGauge(
            LastSuccessGaugeName, Observe, unit: "s",
            description: "日次のクラスタ検出が最後に成功した周期の開始時刻（Unix 秒）。記録が無い間は系列を出さない");
    }

    // 永続化された記録を読んだ／成功を書いたときに呼ぶ。null（記録が無い）は「未観測」へ戻す。
    public void RecordLastSuccess(DateTimeOffset? lastSucceededAt) =>
        Interlocked.Exchange(ref _lastSuccessUnixSeconds,
            lastSucceededAt is { } at ? at.ToUnixTimeSeconds() : Unknown);

    private IEnumerable<Measurement<long>> Observe()
    {
        var value = Interlocked.Read(ref _lastSuccessUnixSeconds);
        return value == Unknown ? [] : [new Measurement<long>(value)];
    }
}
