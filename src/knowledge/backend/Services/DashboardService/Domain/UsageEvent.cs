namespace DashboardService.Domain;

// FR-10, UC-05: 利用イベント（検索実行・AI 回答生成）。
// 利用状況（日次件数）・検索傾向（トップ検索語）の集計元になる。
//
// 🔴 **利用者識別子を持たない**（ADR-0072 決定 1・[[IADR-0368]] (#1198)）。
// SC-10 Q27 が一意利用者数を採らないと決めた理由は「一意集計には利用イベントへ利用者識別子を
// 持たせる必要があり、『誰がいつ何回検索したか』の記録が残る」ことであった。
// **どの集計も識別子を読んでおらず、応答契約にも欄が無い** —— 持っていることの唯一の効果が
// 「記録が残ること」であるなら、それは Q27 が避けた効果そのものである。
//
// **受け口の認証（`RequireAuthorization()`）は維持している。** 認証は不正投入の統制であり、
// 記録の統制とは別である（ADR-0072 決定 1・案 a の却下理由）。
public class UsageEvent
{
    // FR-10: 集計対象の検索語の最大長（カラム長・バリデーションと一致させる）。
    public const int MaxQueryLength = 512;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string EventType { get; private set; } = string.Empty; // search|answer（小文字正規化済み）
    public string? Query { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; } = DateTimeOffset.UtcNow;

    private UsageEvent() { }

    // FR-10: 新規利用イベント。EventType は正規化済み（search|answer）を渡す。
    //   Query は検索傾向の集計に用いる（超過分は切り詰める）。
    // **利用者は引数に取らない**（ADR-0072 決定 1）。
    public static UsageEvent Create(string eventType, string? query)
        => new()
        {
            EventType = eventType,
            Query = Truncate(query, MaxQueryLength),
        };

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
