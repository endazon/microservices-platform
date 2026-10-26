using System.Diagnostics.Metrics;

namespace WikiService.Common.Observability;

// FR-13, NFR-06, [[IADR-0541]] 決定 5 (#1934)・[[IADR-0554]] (#1966): **恒久的な失敗のため、再試行・DLQ に回さず記録だけして終えた Wiki 同期の計数。**
//
// - 計数 `wiki.sync.skipped`（Prometheus では `wiki_sync_skipped_total`）。タグ `reason` は有限の 2 値:
//   - ［2026-10-26 / #2108・[[IADR-0644]]］`slug_conflict` は外した（slug の索引は一意でない。ADR-0091 決定 1）。
//   - `body_missing`: 本文のオブジェクトが無い（NoSuchKey）。切り替え前の文書を再発行すると増える（取り込みの `ingestion.body_missing` と同じ母集合）。
//   - `value_too_long`（[[IADR-0554]] 決定 5・#1966）: Wiki.js が値の長さ（PostgreSQL 22001）でページを拒んだ。表題は切り詰めるので **0 が正常**
//     （増えたら表題以外の列〔タグ等〕が長い。Warning のログで文書を引く）。
//
// - 計数 `wiki.sync.truncated`（Prometheus では `wiki_sync_truncated_total`。[[IADR-0554]] 決定 4・#1966）: 列の長さに収めるため切り詰めて同期した数。
//   タグ `field` は有限の 1 値 `title`（Wiki.js の表題 varchar(255)）。同期そのものは成功している（skipped ではない）。
//
// 🔴 **文書 ID・slug・キーをタグにしない**（基数が無界）。文書ごとの内訳は Warning のログで読む。
public sealed class WikiSyncSkippedMetrics
{
    // Meter 名はサービス名と一致させる（`Program.cs` の `ServiceName`。OTLP の収集対象へ AddMeter する）。
    public const string MeterName = "microservices-platform.wiki-service";

    public const string CounterName = "wiki.sync.skipped";
    public const string ReasonTag = "reason";
    public const string BodyMissing = "body_missing";
    public const string ValueTooLong = "value_too_long";

    public const string TruncatedCounterName = "wiki.sync.truncated";
    public const string FieldTag = "field";
    public const string TitleField = "title";

    private readonly Counter<long> _skipped;
    private readonly Counter<long> _truncated;

    public WikiSyncSkippedMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _skipped = meter.CreateCounter<long>(CounterName, unit: "{document}",
            description: "恒久的な失敗（本文の NoSuchKey・Wiki.js の値の長さ）のため、再試行せずに記録だけして終えた Wiki 同期の数");
        _truncated = meter.CreateCounter<long>(TruncatedCounterName, unit: "{document}",
            description: "Wiki.js の列の長さに収めるため、表題を切り詰めて同期した数");
    }

    public void RecordBodyMissing() => _skipped.Add(1, new KeyValuePair<string, object?>(ReasonTag, BodyMissing));

    public void RecordValueTooLong() => _skipped.Add(1, new KeyValuePair<string, object?>(ReasonTag, ValueTooLong));

    public void RecordTitleTruncated() => _truncated.Add(1, new KeyValuePair<string, object?>(FieldTag, TitleField));
}
