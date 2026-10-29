using System.Diagnostics.Metrics;

namespace IngestionService.Common.Observability;

// FR-02, [[IADR-0650]] (#2283): Qdrant のコレクション・索引の保証の**失敗の回数**。
// 計数 `ingestion.qdrant_bootstrap.failures`（Prometheus では `ingestion_qdrant_bootstrap_failures_total`）。
// 0 が正常。増え続けるなら保証が成功しておらず、取り込みは NotReady のまま再試行している。
public sealed class QdrantBootstrapMetrics
{
    // Meter はサービスで 1 つ（`EmbeddingPendingMetrics.MeterName`。OTLP の収集対象へ AddMeter 済み）。
    public const string MeterName = EmbeddingPendingMetrics.MeterName;

    public const string FailureCounterName = "ingestion.qdrant_bootstrap.failures";

    private readonly Counter<long> _failures;

    public QdrantBootstrapMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _failures = meter.CreateCounter<long>(FailureCounterName, unit: "{attempt}",
            description: "Qdrant のコレクション・索引の保証に失敗した回数（成功するまで上限つきのバックオフで再試行する）");
    }

    public void RecordFailure() => _failures.Add(1);
}
