namespace NotificationService.Domain;

// FR-22, ADR-0037 決定 6・17・18, IADR-0215: 通知の種別。
//
// ★ **閉じた enum にしない**（IADR-0215 決定 2 / BFF_bff-surface.md §横断の規約 4）。
// 契約側が `type: string` で開いている以上、後段の型も開いていなければ意味が無い ——
// 閉じると「種別を増やしたら未デプロイの読み手が既存の値も解釈できなくなる」を
// 後段の側で再現してしまう。値集合はここに**定数として**置き、検証はしない。
public static class NotificationKinds
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

// FR-22: アプリ内通知 1 件。
//
// ★ **タイトル・本文に相当するフィールドを 1 つも持たない。** 計画 FR-22 の受け入れ基準
// 「本文が件数と期限のみで構成される。資料のタイトル・本文・検索語・回答内容を含まない」を
// **実装の規律ではなく型の形**で守らせる（IADR-0215 決定 2）。**メールは本システムの ABAC の
// 外側へ出る**ため、ここが最も守られるべき境界である。自由文の口を 1 つでも開けると、
// いつか誰かがそこへ資料のタイトルを入れる。
public class Notification
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    // ★ 宛先。**所有者本人ただ 1 人**である（FR-22 / ADR-0037 決定 6）。JWT の主体（sub）と突き合わせる。
    // 利用者 × 通知の関連表は持たない —— 通知は最初から 1 人に属する（IADR-0215 決定 2）。
    public string Subject { get; private set; } = string.Empty;

    public string Kind { get; private set; } = string.Empty;
    public int? Count { get; private set; }
    public int? ThresholdPercent { get; private set; }
    public DateTimeOffset? Deadline { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; } = DateTimeOffset.UtcNow;
    public bool Read { get; private set; }

    private Notification() { }

    public static Notification Create(
        string subject, string kind, DateTimeOffset occurredAt,
        int? count = null, int? thresholdPercent = null, DateTimeOffset? deadline = null)
        => new()
        {
            Subject = subject,
            Kind = kind,
            OccurredAt = occurredAt,
            Count = count,
            ThresholdPercent = thresholdPercent,
            Deadline = deadline,
        };

    // 既読化は冪等である（既読のものへもう一度呼んでも状態は変わらない）。
    public void MarkRead() => Read = true;
}

// FR-22, ADR-0045, IADR-0215 決定 3・4: メール送信要求の outbox エントリ。
//
// **アプリ内通知の永続化とは別トランザクションで積む。** メール側が何をしても
// アプリ内通知の成否には触れられない —— これが「メールが送れなくてもアプリ内通知は届く」の実体である。
public class EmailOutboxEntry
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    // 対応するアプリ内通知。**外部キー制約は張らない**（保持期間の経過で通知が消えても、
    // 送出の記録＝観測面は残す必要がある。IADR-0267 決定 3）。
    public Guid NotificationId { get; private set; }

    public string Subject { get; private set; } = string.Empty;
    public string Kind { get; private set; } = string.Empty;

    // 本文の材料。**ここにも自由文は無い**（Notification と同じ理由）。
    public int? Count { get; private set; }
    public int? ThresholdPercent { get; private set; }

    // 期限。**繰り越しの打ち切り条件でもある** —— 期限を過ぎた繰り越しは意味を失うので破棄する
    // （IADR-0215 決定 4 の例外）。
    public DateTimeOffset? Deadline { get; private set; }

    public string Status { get; private set; } = EmailOutboxStatus.Pending;
    public int AttemptCount { get; private set; }
    public int DeferralCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? LastOutcomeAt { get; private set; }

    // 結末の理由。**利用者の資料に由来する文字列を入れない**（種別と機械的な理由語だけ）。
    public string? LastReason { get; private set; }

    private EmailOutboxEntry() { }

    public static EmailOutboxEntry For(Notification notification)
        => new()
        {
            NotificationId = notification.Id,
            Subject = notification.Subject,
            Kind = notification.Kind,
            Count = notification.Count,
            ThresholdPercent = notification.ThresholdPercent,
            Deadline = notification.Deadline,
            CreatedAt = notification.OccurredAt,
        };

    public void MarkSent(DateTimeOffset at)
    {
        Status = EmailOutboxStatus.Sent;
        AttemptCount++;
        SentAt = at;
        LastOutcomeAt = at;
        LastReason = null;
    }

    // 上限に触れた。**送らずに繰り越す**（ADR-0045 §結果 フォローアップ 8 への回答＝ IADR-0215 決定 4）。
    public void Defer(DateTimeOffset at, string reason)
    {
        Status = EmailOutboxStatus.Deferred;
        DeferralCount++;
        LastOutcomeAt = at;
        LastReason = reason;
    }

    // 期限を過ぎた繰り越しは破棄する。**黙って消さない** —— 状態と理由を残す。
    public void Drop(DateTimeOffset at, string reason)
    {
        Status = EmailOutboxStatus.Dropped;
        LastOutcomeAt = at;
        LastReason = reason;
    }

    // 送信そのものが失敗した。**「設定が無いから何もしない」は failed であって成功ではない。**
    public void Fail(DateTimeOffset at, string reason)
    {
        Status = EmailOutboxStatus.Failed;
        AttemptCount++;
        LastOutcomeAt = at;
        LastReason = reason;
    }
}

// FR-22, ADR-0045 決定 8: outbox の結末。**4 つとも監査ログとメトリクスに載る**
// （受け入れ基準「送信上限を超える通知が静かに落ちない」）。
public static class EmailOutboxStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Deferred = "deferred";
    public const string Dropped = "dropped";
    public const string Failed = "failed";
}
