namespace GraphService.Domain.Ports;

// FR-10, FR-17, SC-10, ADR-0006, IADR-0265, [[IADR-0299]] (#443): ナレッジ健全性の観測値の送出口。
//
// **本サービスは指標の「生産者」であり、集計主体ではない**（集計は DashboardService。
// DB-per-service〔ADR-0002〕のため、あちらから `graph_documents` / `edges` を直接は数えられない）。
//
// 🔴 **件数ではなく観測値を渡す。** 個人資料を集計から外す規則は**受け手が強制する**設計であり
// （IADR-0265）、件数だけを渡すと「除外したかどうか」を受け手が確かめられない。
// 生産者の責務は**対象の集合と、各対象の文書スコープを正しく添えること**である。
public interface IKnowledgeHealthReporter
{
    // 指標 1 つ分の観測値を**全量スナップショットとして**報告する。
    //
    // 🔴 **空でも呼ぶこと。** 受け口は当該指標の既存行を落としてから差し替えるため、
    // 「0 件だから送らない」と最適化すると**前回の件数が恒久的に残る**（解消したのに数字が減らない）。
    //
    // `thresholdDays` — 判定に使った日数のしきい値。**持たない指標では null**。
    //   planning#494 決定 3「SC-10 には件数と現在のしきい値を併記する」を、画面が読める形に
    //   するために運ぶ（[[IADR-0357]] 決定 4）。🔴 **観測値 1 件ごとの属性ではなく報告 1 通の属性**
    //   である —— 件数が 0 のとき観測値は 1 件も無く、そこへ乗せると**しきい値も一緒に消える**。
    Task ReportAsync(
        string indicator,
        IReadOnlyList<KnowledgeHealthObservation> observations,
        int? thresholdDays = null,
        CancellationToken ct = default);
}

// 観測値 1 件。
//   SubjectKey — 重複排除のための不透明な鍵（文書 ID）。**受け口は応答に出さない。**
//   DocScope   — 文書スコープ。個人資料は `private-note`、それ以外は null。
//                受け手が集計前にこの値で個人資料を落とす。
public readonly record struct KnowledgeHealthObservation(string SubjectKey, string? DocScope);
