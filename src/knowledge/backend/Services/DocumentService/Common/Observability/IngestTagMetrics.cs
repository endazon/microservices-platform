using System.Diagnostics.Metrics;

namespace DocumentService.Common.Observability;

// FR-01, SC-05, SC-09, SC-10, #637: **取り込み経路で辞書に無いタグが現れた件数**
// （計画確定・2026-08-09。利用者裁定 planning#304。
// 計画 `06_technical/05_observability-ops.md` §ナレッジ健全性の指標）。
//
// **0 が正常である。** 同じ表に並ぶ他の 6 指標は「発生してよい事象の量」を測るものだが、
// **本指標は発生してはならない事象の件数**である——**取り込み経路はタグを生成しない**と
// 計画が確定しているため（[[IADR-0153]] 決定 5）。
// **したがって 0 でない値は「計画に無い経路でタグが生まれている」ことの検出になる。**
//
// **SC-10 の画面へは出さない。** 「ナレッジ健全性」節は [[IADR-0119]] により**節ごと着手保留**であり
// （FR-17 が保留。`OperationsDashboardPage` が節を出さないことをテストで固定している）、
// ここに 1 指標だけ差し込むと保留の線引きが壊れる。
// **裁定が求めたのは「0 でない値が検出になる」ことであり、Grafana で観測できれば成立する。**
// 画面の行は、保留が解けて同節を作るときに一緒に置く。
public sealed class IngestTagMetrics
{
    // Meter 名はサービス名と一致させる（OTLP の収集対象）。
    public const string MeterName = "microservices-platform.document-service";
    public const string UnknownTagCounterName = "ingest.unknown_tag.total";

    // どの経路から来たかを見分ける（将来コネクタがタグを運ぶ場合に出どころを追える）。
    public const string SourceTag = "ingest.source";

    private readonly Counter<long> _unknownTags;

    public IngestTagMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _unknownTags = meter.CreateCounter<long>(
            UnknownTagCounterName,
            unit: "{tag}",
            description: "取り込み経路で辞書に無いタグが現れた件数（0 が正常）");
    }

    // **辞書に無いタグが取り込み経路で現れた**ことを記録する。
    // **タグそのものは文書へ付けない**——SC-05 の「既定タグ辞書に整合」は**経路を問わない不変条件**である。
    public void RecordUnknownTag(string source) =>
        _unknownTags.Add(1, new KeyValuePair<string, object?>(SourceTag, source));
}
