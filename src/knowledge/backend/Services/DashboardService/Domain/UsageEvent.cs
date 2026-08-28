namespace DashboardService.Domain;

// FR-10, UC-05: 利用イベント（検索実行・AI 回答生成）。
// 利用状況（日次件数）・検索傾向（トップ検索語）の集計元になる。
public class UsageEvent
{
    // FR-10: 集計対象の検索語の最大長（カラム長・バリデーションと一致させる）。
    public const int MaxQueryLength = 512;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string EventType { get; private set; } = string.Empty; // search|answer（小文字正規化済み）
    public string? Query { get; private set; }
    public string UserId { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; } = DateTimeOffset.UtcNow;

    private UsageEvent() { }

    // FR-10: 新規利用イベント。EventType は正規化済み（search|answer）を渡す。
    //   Query は検索傾向の集計に用いる（超過分は切り詰める）。
    public static UsageEvent Create(string eventType, string? query, string userId)
        => new()
        {
            EventType = eventType,
            Query = Truncate(query, MaxQueryLength),
            UserId = userId,
        };

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
