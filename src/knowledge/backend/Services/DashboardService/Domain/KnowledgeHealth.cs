namespace DashboardService.Domain;

// FR-10, FR-17, FR-18, UC-05, SC-10, ADR-0006 (#443): ナレッジ健全性の指標の語彙。
// 計画 `06_technical/05_observability-ops.md` §ナレッジ健全性の指標（集計範囲・2026-08-02 確定）が
// 定める **7 指標**をそのまま持つ。**値域は閉じる**（未知の指標名は 400）——
// 語彙が開いていると、生産者側の綴り違いが「0 件の指標」として静かに現れ、
// 「指標が改善した」と読めてしまうためである。
public static class KnowledgeHealthIndicators
{
    // 孤立文書数: どの文書からも参照されず、どの文書も参照していない文書。
    public const string OrphanDocuments = "orphan-documents";

    // 解決できないリンク数: リンク先を特定できない辺。
    public const string UnresolvedLinks = "unresolved-links";

    // 未要約クラスタ数: 要約が生成されていないクラスタ（コミュニティ）。
    public const string UnsummarizedClusters = "unsummarized-clusters";

    // 陳腐化文書数: **本文**の更新が一定期間途絶えている文書。
    // ★［2026-09-03 / #1186］planning#494 が **180 日**（初期値）・**起点は本文の更新のみ**・
    // **配備時の構成で変更できる**を確定させた。**判定と現在のしきい値は生産者側が持つ**
    // （しきい値は報告 1 通の属性として運ばれ、KnowledgeHealthIndicatorThreshold に写る）。
    public const string StaleDocuments = "stale-documents";

    // 辺の型ごとの使用件数（ADR-0033 決定 9）。
    public const string EdgeTypeUsage = "edge-type-usage";

    // 未定義型のフォールバック警告件数（ADR-0033 決定 3）。
    public const string UndefinedTypeFallbacks = "undefined-type-fallbacks";

    // 取り込み経路で辞書に無いタグが現れた件数。
    // ⚠️ **この 1 指標だけ読み方が違う。0 が正常である**（取り込み経路はタグを生成しないと計画が確定した。
    // 裁定 planning#304）。他の 6 指標は「発生してよい事象の量」である。
    public const string IngestUnknownTags = "ingest-unknown-tags";

    // 応答の並び順の正本（計画の表の並び）。**0 件の指標も欠落させない**——
    // 指標が消えたのか 0 なのかを画面が区別できなくなる。
    public static readonly IReadOnlyList<string> All =
    [
        OrphanDocuments,
        UnresolvedLinks,
        UnsummarizedClusters,
        StaleDocuments,
        EdgeTypeUsage,
        UndefinedTypeFallbacks,
        IngestUnknownTags,
    ];

    public static bool IsValid(string? indicator)
        => indicator is not null && All.Contains(indicator, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string indicator) => indicator.ToLowerInvariant();
}

// FR-19, ADR-0054 決定 1・2 (#443): 文書スコープ（ABAC 属性 `doc_scope` の値）。
// 綴りは計画 ADR が確定させたものをそのまま用いる（実装で言い換えない）。
public static class KnowledgeDocScopes
{
    public const string PrivateNote = "private-note";

    // 🔴 **集合帰属で判定する。「organization でない」で書いてはならない。**
    // `doc_scope` を持たない文書（実データの大半）が個人資料と見なされ、
    // **健全性指標が一斉に 0 になる**（WikiService の同期除外が同じ理由で集合帰属を採っている）。
    public static bool IsPrivateNote(string? docScope)
        => string.Equals(docScope, PrivateNote, StringComparison.OrdinalIgnoreCase);
}

// FR-10, FR-17, FR-18, SC-10 (#443): ナレッジ健全性の観測値 1 件。
//
// **なぜ件数ではなく観測値を持つのか**: 除外規則（個人資料を集計から外す）を**集計する側で強制する**
// ためである。生産者から件数だけを受け取ると、除外したかどうかを受け手が確かめられず、
// 「除外し忘れた件数」と「除外済みの件数」が区別できない。
//
// **SubjectKey は API から一切返さない。** 計画は「個々の文書名を出さず、件数のみを示す」と定めており、
// 文書名を出すと閲覧ロールを限定していても ABAC の文書単位判定を迂回して個々の文書の存在が伝わる。
// ここで保持するのは重複排除のための不透明な鍵であり、表示のためのものではない。
public class KnowledgeHealthObservation
{
    public const int MaxIndicatorLength = 64;
    public const int MaxSubjectKeyLength = 256;
    public const int MaxDocScopeLength = 64;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Indicator { get; private set; } = string.Empty;
    public string SubjectKey { get; private set; } = string.Empty;
    public string? DocScope { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; } = DateTimeOffset.UtcNow;

    private KnowledgeHealthObservation() { }

    public static KnowledgeHealthObservation Create(
        string indicator, string subjectKey, string? docScope, DateTimeOffset observedAt)
        => new()
        {
            Indicator = KnowledgeHealthIndicators.Normalize(indicator),
            SubjectKey = Truncate(subjectKey, MaxSubjectKeyLength),
            DocScope = docScope is null ? null : Truncate(docScope.Trim().ToLowerInvariant(), MaxDocScopeLength),
            ObservedAt = observedAt,
        };

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}

// FR-10, UC-05, SC-10, planning#494 決定 3, [[IADR-0353]] (#1186):
// 指標ごとの**現在のしきい値**。いまは陳腐化文書数（日数）だけが持つ。
//
// 🔴 **観測値の行に持たせない。** 観測値は指標 1 つ分の全量スナップショットであり、
// **件数が 0 のときは 1 行も無い**。そこへ持たせるとしきい値も一緒に消え、
// 計画が求めた「件数と現在のしきい値を併記する」が **0 件のときにだけ満たせなくなる**
// ——「0 件」は最も表示したい状態であり、そこで欠けるのは本末転倒である。
//
// 生産者が報告のたびに置き換える（**しきい値を添えない報告では行を消す**。観測値と同じ姿勢）。
public class KnowledgeHealthIndicatorThreshold
{
    public string Indicator { get; private set; } = string.Empty;

    // 日数。**0 以下は受け付けない**（受け口が 400 で落とす）。
    public int ThresholdDays { get; private set; }

    public DateTimeOffset ReportedAt { get; private set; } = DateTimeOffset.UtcNow;

    private KnowledgeHealthIndicatorThreshold() { }

    public static KnowledgeHealthIndicatorThreshold Create(
        string indicator, int thresholdDays, DateTimeOffset reportedAt)
        => new()
        {
            Indicator = KnowledgeHealthIndicators.Normalize(indicator),
            ThresholdDays = thresholdDays,
            ReportedAt = reportedAt,
        };

    public void Update(int thresholdDays, DateTimeOffset reportedAt)
    {
        ThresholdDays = thresholdDays;
        ReportedAt = reportedAt;
    }
}
