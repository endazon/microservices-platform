using System.Diagnostics.Metrics;

namespace WikiService.Common.Observability;

// FR-13, NFR-06, [[IADR-0541]] 決定 5 (#1934): **恒久的な失敗のため、再試行・DLQ に回さず記録だけして終えた Wiki 同期の計数。**
//
// - 計数 `wiki.sync.skipped`（Prometheus では `wiki_sync_skipped_total`）。タグ `reason` は有限の 2 値:
//   - `slug_conflict`: 文書 ID 由来の接尾辞へ付け替えても slug の一意制約（`IX_Pages_Slug`）に当たった。**0 が正常**（起こり得るのは人手で同じ slug を書いた行があるときだけ）。
//   - `body_missing`: 本文のオブジェクトが無い（NoSuchKey）。切り替え前の文書を再発行すると増える（取り込みの `ingestion.body_missing` と同じ母集合）。
//
// 🔴 **文書 ID・slug・キーをタグにしない**（基数が無界）。文書ごとの内訳は Warning のログで読む。
public sealed class WikiSyncSkippedMetrics
{
    // Meter 名はサービス名と一致させる（`Program.cs` の `ServiceName`。OTLP の収集対象へ AddMeter する）。
    public const string MeterName = "microservices-platform.wiki-service";

    public const string CounterName = "wiki.sync.skipped";
    public const string ReasonTag = "reason";
    public const string SlugConflict = "slug_conflict";
    public const string BodyMissing = "body_missing";

    private readonly Counter<long> _skipped;

    public WikiSyncSkippedMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _skipped = meter.CreateCounter<long>(CounterName, unit: "{document}",
            description: "恒久的な失敗（slug の一意制約・本文の NoSuchKey）のため、再試行せずに記録だけして終えた Wiki 同期の数");
    }

    public void RecordSlugConflict() => _skipped.Add(1, new KeyValuePair<string, object?>(ReasonTag, SlugConflict));

    public void RecordBodyMissing() => _skipped.Add(1, new KeyValuePair<string, object?>(ReasonTag, BodyMissing));
}
