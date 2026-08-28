namespace DocumentService.Domain.Ports;

// FR-22, ADR-0037 決定 6・17・18, IADR-0215 決定 5, [[IADR-0270]] 決定 6:
// 個人資料まわりの通知の**発火側ポート**。検知（いつ・誰へ・何件）は DocumentService が行い、
// 通知の実体（アプリ内保持・既読・メール outbox・送信レート）は NotificationService が担う。
//
// ★ **自由文の引数を 1 つも持たない。** FR-22「本文が件数と期限のみで構成される。資料のタイトル・
// 本文を含まない」を、呼び出し規約ではなく**型の形**で守る（NotificationService 側の Notification
// エンティティと同じ設計。IADR-0215 決定 2）。
public interface IPrivateNoteNotifier
{
    // subject: 宛先（所有者本人のみ）。kind: PrivateNoteNotificationKinds の値。
    // count: 件数（①③）。thresholdPercent: 容量警告の閾値（②）。deadline: 期限（①-a/①-b/③）。
    Task NotifyAsync(string subject, string kind, DateTimeOffset occurredAt,
        int? count = null, int? thresholdPercent = null, DateTimeOffset? deadline = null,
        CancellationToken ct = default);
}

// FR-22: 通知種別。🔴 **NotificationService の NotificationKinds と文字列一致させる**
// （platform 側プロジェクトへの参照は張らないため定数を複製し、値の一致はテストで固定する。
// [[IADR-0270]] 決定 6）。
public static class PrivateNoteNotificationKinds
{
    // ①-a: 論理削除済み個人資料の週次通知（件数 ＋ 最短の完全削除期限）
    public const string PrivateNotePurgeWeekly = "private-note-purge-weekly";

    // ①-b: 完全削除の 7 日前（件数 ＋ 期限）
    public const string PrivateNotePurgeImminent = "private-note-purge-imminent";

    // ①-c: 完全削除の事後（件数のみ）
    public const string PrivateNotePurgeDone = "private-note-purge-done";

    // ②: 保存容量が 80% / 95% に達した（閾値のみ）
    public const string StorageQuotaWarning = "storage-quota-warning";

    // ③: 同期トークンの期限 7 日前（件数 ＋ 期限）
    public const string SyncTokenExpiry = "sync-token-expiry";
}
