namespace Knowledge.Contracts.Dtos;

// FR-10, UC-05: 利用状況・検索傾向・回答品質を可視化するダッシュボード用の DTO 群。

// FR-10: 利用イベントの種別。検索実行と AI 回答生成を記録する。
public static class UsageEventType
{
    public const string Search = "search";
    public const string Answer = "answer";

    public static bool IsValid(string? type)
        => string.Equals(type, Search, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, Answer, StringComparison.OrdinalIgnoreCase);

    // 入力の大小揺れを正規化（保存・集計は小文字で統一する）。
    public static string Normalize(string type) => type.ToLowerInvariant();
}

// FR-10: 利用イベント記録リクエスト。
//   EventType — "search"（検索実行）/ "answer"（AI 回答生成）のいずれか。
//   Query     — 検索語（種別が search のとき任意。検索傾向の集計に用いる）。
public record UsageEventRequest(string EventType, string? Query = null);

// FR-10: 日次利用状況の 1 点（日付 × 種別の件数）。利用状況の折れ線グラフの入力。
public record UsagePointDto(DateOnly Date, string EventType, int Count);

// FR-10: 検索傾向の 1 点（検索語 × 件数）。よく検索される語の可視化に用いる。
//
// 🔴 **しきい値をここへ持たせない**（ADR-0071 決定 2 / [[IADR-0357]] 決定 2）——
// **しきい値で伏せた結果としてこの行は 0 件になり得る**。0 件はしきい値の効果が最も強く出た状態であり、
// そこで併記が消えるのは本末転倒である。しきい値は下の封筒 2 つが運ぶ。
public record SearchTrendDto(string Term, int Count);

// FR-10: DashboardService が返す利用側サマリ（総件数・利用状況・検索傾向）。
//   回答品質（FeedbackService 由来）は含まず、BFF が付加して DashboardSummaryDto を組み立てる。
//   SearchTermMinCount — 検索傾向の出現件数の下限（ADR-0071 決定 1・2。#1197）。
public record DashboardUsageDto(
    int TotalSearches,
    int TotalAnswers,
    IReadOnlyList<UsagePointDto> UsageTrend,
    IReadOnlyList<SearchTrendDto> TopSearchTerms,
    // ADR-0071 決定 2: 画面へ現在のしきい値を併記するために運ぶ。
    // **既定値 0 を付けるのは 2 つの理由による。** (1) 既定値の無いメンバーの追加は
    // `check-contract-schema.js` が破壊的と分類する（[[IADR-0122]] 決定 2）。
    // (2) 🔴 **0 の向きが安全側である** —— 本項目を返さない旧 `DashboardService` が
    // 後段に居る配備では、逆直列化が 0 を入れ、画面のふるい落としは素通りになる。
    // 既定を 3 にすると、しきい値を知らない応答から画面が勝手に語を消す。
    int SearchTermMinCount = 0);

// FR-10: ダッシュボードのサマリ。利用状況・検索傾向・回答品質を 1 応答に集約する。
//   TotalSearches / TotalAnswers — 対象期間の総件数。
//   UsageTrend    — 日次件数（利用状況）。
//   TopSearchTerms — 上位検索語（検索傾向）。**出現件数が SearchTermMinCount 未満の語は含まれない。**
//   Quality       — 回答品質（👍/👎 件数・満足率。FR-08 FeedbackService 由来）。
//   SearchTermMinCount — 検索傾向の出現件数の下限（ADR-0071 決定 1・2。#1197）。
public record DashboardSummaryDto(
    int TotalSearches,
    int TotalAnswers,
    IReadOnlyList<UsagePointDto> UsageTrend,
    IReadOnlyList<SearchTrendDto> TopSearchTerms,
    FeedbackStatsDto Quality,
    int SearchTermMinCount = 0);
