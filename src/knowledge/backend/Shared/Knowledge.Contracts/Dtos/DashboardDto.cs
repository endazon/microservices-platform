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
public record SearchTrendDto(string Term, int Count);

// FR-10: DashboardService が返す利用側サマリ（総件数・利用状況・検索傾向）。
//   回答品質（FeedbackService 由来）は含まず、BFF が付加して DashboardSummaryDto を組み立てる。
public record DashboardUsageDto(
    int TotalSearches,
    int TotalAnswers,
    IReadOnlyList<UsagePointDto> UsageTrend,
    IReadOnlyList<SearchTrendDto> TopSearchTerms);

// FR-10: ダッシュボードのサマリ。利用状況・検索傾向・回答品質を 1 応答に集約する。
//   TotalSearches / TotalAnswers — 対象期間の総件数。
//   UsageTrend    — 日次件数（利用状況）。
//   TopSearchTerms — 上位検索語（検索傾向）。
//   Quality       — 回答品質（👍/👎 件数・満足率。FR-08 FeedbackService 由来）。
public record DashboardSummaryDto(
    int TotalSearches,
    int TotalAnswers,
    IReadOnlyList<UsagePointDto> UsageTrend,
    IReadOnlyList<SearchTrendDto> TopSearchTerms,
    FeedbackStatsDto Quality);
