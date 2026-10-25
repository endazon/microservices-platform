using System.Diagnostics.Metrics;

namespace IngestionService.Common.Observability;

// FR-02, UC-04, [[IADR-0540]] 決定 3 (#1931): **本文のオブジェクトが無かった（S3 の NoSuchKey）取り込みの計数。**
//
// - 計数 `ingestion.body_missing`（Prometheus では `ingestion_body_missing_total`）: 本文が無いため記録だけして終えた
//   `DocumentUpdated` の数。**0 が正常である**（9/26 の SeaweedFS 切り替えより前の文書を再索引に流すと増える）。
//
// 🔴 **文書 ID・キーをタグにしない**（基数が無界）。文書ごとの内訳は Warning のログで読む。
public sealed class BodyMissingMetrics
{
    // Meter はサービスで 1 つ（`EmbeddingPendingMetrics.MeterName`＝サービス名。OTLP の収集対象へ AddMeter 済み）。
    public const string MeterName = EmbeddingPendingMetrics.MeterName;

    public const string CounterName = "ingestion.body_missing";

    private readonly Counter<long> _missing;

    public BodyMissingMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _missing = meter.CreateCounter<long>(CounterName, unit: "{document}",
            description: "本文のオブジェクトが無く（NoSuchKey）、索引せずに記録だけして終えた文書の数");
    }

    public void RecordMissing() => _missing.Add(1);
}
