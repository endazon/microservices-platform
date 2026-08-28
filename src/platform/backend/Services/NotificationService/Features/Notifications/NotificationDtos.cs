namespace NotificationService.Features.Notifications;

// FR-22, IADR-0215 決定 2: BFF が `/bff/notifications` でそのまま返す形。
//
// ★ **項目は 7 つだけで、自由文のフィールドは 1 つも無い。**
// `docs/api/openapi.yaml` の `NotificationDto` と同じ集合であり、**増やすと契約が割れる**。
// フロント側の契約テスト（notificationContract.test.ts）と、本サービスの契約テストの
// 両方がこの集合を固定する。
//
// **`Kind` は string である**（閉じた enum にしない。IADR-0215 決定 2）。
public sealed record NotificationDto(
    Guid Id,
    string Kind,
    int? Count,
    int? ThresholdPercent,
    DateTimeOffset? Deadline,
    DateTimeOffset OccurredAt,
    bool Read);

// FR-22: 本人宛の一覧と未読件数。
// **`UnreadCount` は絞り込み（unreadOnly / limit）の影響を受けない全体値である**（契約の記述どおり）。
public sealed record NotificationListDto(
    IReadOnlyList<NotificationDto> Items,
    int UnreadCount);

// FR-22: 既読化の結果。更新後の未読件数を返す（バッジだけを更新したい呼び出し元のため）。
public sealed record NotificationReadResultDto(Guid Id, int UnreadCount);
