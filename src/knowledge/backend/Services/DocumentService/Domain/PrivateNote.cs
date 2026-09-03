namespace DocumentService.Domain;

// FR-19, FR-20, UC-11, ADR-0037 決定 3〜5・16〜20, ADR-0054, [[IADR-0270]] 決定 2:
// 個人資料（private-note）の台帳。Document（doc_scope=private-note）と 1:1 で対応し、
// FR-19 固有の状態（最新版バイト数・論理削除／完全削除期限・露出 3 トグル・Vault パス）を持つ。
//
// **Document へ列を足さず台帳に分けるのは、Document を読む全消費面（イベント・DTO・射影）の
// 契約へ波及させないためである**（DocumentShare と同じ分離。IADR-0253 決定 4）。
//
// **容量の算入規則（ADR-0037 決定 16・19）はこの台帳の形そのもので守る** ——
// `LatestBytes` は**最新版のバイト数だけ**を持ち（版履歴のバイト数を持つ場所が無い＝算入しようがない）、
// 論理削除しても行が残る（＝算入され続ける）。行が消えるのは完全削除（purge）のときだけである。
public class PrivateNote
{
    // ADR-0037 決定 5・16: 論理削除の保管期間と版履歴の日数条件（いずれも 90 日）。
    public const int RetentionDays = 90;

    // ADR-0037 決定 16: 版履歴の保持上限（1 資料あたり直近 50 版）。
    public const int VersionKeepCount = 50;

    public Guid DocumentId { get; private set; }
    public string OwnerId { get; private set; } = string.Empty;

    // FR-20, ADR-0037 決定 4: Obsidian Vault 内の相対パス（対象フォルダ配下）。同期の突き合わせキー。
    public string VaultPath { get; private set; } = string.Empty;

    // FR-19: 最新版の本文サイズ（UTF-8 バイト数）。容量の算入単位（[[IADR-0270]] 決定 4）。
    public long LatestBytes { get; private set; }

    // FR-20: 最新版本文の SHA-256（hex）。プラグインの差分判定用（本文そのものは持たない）。
    public string? ContentHash { get; private set; }

    // FR-19: 露出 3 トグル（横断検索／ナレッジグラフ／AI 入力）。**既定はいずれも OFF**。
    // ON の消費側配線は IADR-0253 段 3 の完了待ちであり、本段では保存のみ（[[IADR-0270]] 決定 5）。
    public bool IncludeInSearch { get; private set; }
    public bool IncludeInGraph { get; private set; }
    public bool IncludeInAi { get; private set; }

    // FR-19, ADR-0037 決定 5: 論理削除。90 日間は復元でき、PurgeAt 経過で自動物理削除される。
    public DateTimeOffset? DeletedAt { get; private set; }
    public DateTimeOffset? PurgeAt { get; private set; }

    // FR-22, ADR-0037 決定 6-②: 完全削除 7 日前通知の発火記録（1 回だけ送るため）。
    public DateTimeOffset? PurgeImminentNotifiedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private PrivateNote() { }

    public static PrivateNote Create(Guid documentId, string ownerId, string vaultPath,
        long latestBytes, string? contentHash, DateTimeOffset now) => new()
        {
            DocumentId = documentId,
            OwnerId = ownerId,
            VaultPath = vaultPath,
            LatestBytes = latestBytes,
            ContentHash = contentHash,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public bool IsDeleted => DeletedAt is not null;

    // FR-20: 本文の更新（同期 push）。最新版のバイト数とハッシュだけを追随させる。
    public void RecordBody(long latestBytes, string contentHash, DateTimeOffset now)
    {
        LatestBytes = latestBytes;
        ContentHash = contentHash;
        UpdatedAt = now;
    }

    // FR-20, ADR-0037 決定 2, [[IADR-0360]] 決定 2: Obsidian 側のリネームの伝播（同期 move）。
    // **版は進めない** —— `VaultPath` は台帳の項目であって `Document` の版ではなく、本文も
    // 変わっていない。名前の変更で版履歴（直近 50 版）を使い切らせない。
    // 一意性（有効な行の中で所有者ごとに一意）は呼び出し側が判定する（新規作成と同じ関数）。
    public void MoveTo(string vaultPath, DateTimeOffset now)
    {
        VaultPath = vaultPath;
        UpdatedAt = now;
    }

    // FR-19: 露出 3 トグルの変更（SC-20 露出設定）。
    public void SetExposure(bool includeInSearch, bool includeInGraph, bool includeInAi,
        DateTimeOffset now)
    {
        IncludeInSearch = includeInSearch;
        IncludeInGraph = includeInGraph;
        IncludeInAi = includeInAi;
        UpdatedAt = now;
    }

    // FR-19, ADR-0037 決定 5: 論理削除。**容量は減らない**（行が残り LatestBytes が算入され続ける。
    // 決定 19「論理削除を実行しても残容量は増えない」）。冪等（削除済みへの再削除は期限を延ばさない）。
    public void SoftDelete(DateTimeOffset now)
    {
        if (IsDeleted) return;
        DeletedAt = now;
        PurgeAt = now.AddDays(RetentionDays);
        PurgeImminentNotifiedAt = null;
        UpdatedAt = now;
    }

    // FR-19: 復元（90 日以内）。purge 済みは行が無いため到達しない。
    public void Restore(DateTimeOffset now)
    {
        DeletedAt = null;
        PurgeAt = null;
        PurgeImminentNotifiedAt = null;
        UpdatedAt = now;
    }

    public void MarkPurgeImminentNotified(DateTimeOffset now) => PurgeImminentNotifiedAt = now;
}

// FR-19, NFR-27, ADR-0037 決定 16・17, [[IADR-0270]] 決定 4: 利用者ごとの保存容量。
// 既定 1 GB・管理者が最大 1 TB まで引き上げ可。80% / 95% の警告は**跨ぎ判定**で各 1 回とし、
// 閾値を下回ったら再武装する（IADR-0215 決定 5 ②）。
public class PrivateNoteQuota
{
    public const long DefaultLimitBytes = 1L * 1024 * 1024 * 1024;          // 1 GB
    public const long MaxLimitBytes = 1024L * 1024 * 1024 * 1024;           // 1 TB
    public const int WarnPercentLow = 80;
    public const int WarnPercentHigh = 95;

    public string OwnerId { get; private set; } = string.Empty;
    public long LimitBytes { get; private set; } = DefaultLimitBytes;

    // FR-22 ②: 80% / 95% 警告の発火記録（跨ぎで 1 回。下回ったら解除）。
    public bool Warned80 { get; private set; }
    public bool Warned95 { get; private set; }

    // FR-22 ①-a: 週次通知の送出記録（7 日間隔の下限。[[IADR-0270]] 決定 6）。
    public DateTimeOffset? WeeklyDigestSentAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private PrivateNoteQuota() { }

    public static PrivateNoteQuota Create(string ownerId, DateTimeOffset now) => new()
    {
        OwnerId = ownerId,
        UpdatedAt = now,
    };

    // FR-19: 管理者による上限変更（SC-19 運用）。1 バイト以上・最大 1 TB。
    public void SetLimit(long limitBytes, DateTimeOffset now)
    {
        if (limitBytes <= 0 || limitBytes > MaxLimitBytes)
            throw new ArgumentOutOfRangeException(nameof(limitBytes),
                $"保存容量の上限は 1 バイト以上 {MaxLimitBytes} バイト（1 TB）以下です。");
        LimitBytes = limitBytes;
        UpdatedAt = now;
    }

    public int PercentOf(long usedBytes)
        => LimitBytes <= 0 ? 100 : (int)(usedBytes * 100 / LimitBytes);

    // FR-19, ADR-0037 決定 17: 新規作成の拒否判定。**更新はこの判定を通らない**（100% でも許す）。
    // 上限を跨ぐ新規作成も拒否する（超過分は「最新版の増分」= 更新に限る。[[IADR-0270]] 決定 4）。
    public bool RejectsNewNote(long usedBytes, long newNoteBytes)
        => usedBytes >= LimitBytes || usedBytes + newNoteBytes > LimitBytes;

    // FR-22 ②: 使用量の変化を記録し、新たに跨いだ警告閾値（80 / 95）を返す。
    // 閾値を下回ったら発火記録を解除する（容量は上下するため。IADR-0215 決定 5 ②）。
    public IReadOnlyList<int> RecordUsage(long usedBytes, DateTimeOffset now)
    {
        var percent = PercentOf(usedBytes);
        var crossed = new List<int>();

        if (percent >= WarnPercentHigh)
        {
            if (!Warned95) { Warned95 = true; crossed.Add(WarnPercentHigh); }
            if (!Warned80) Warned80 = true; // 95 以上は 80 も跨いでいる（80 の重複通知はしない）
        }
        else if (percent >= WarnPercentLow)
        {
            if (!Warned80) { Warned80 = true; crossed.Add(WarnPercentLow); }
            Warned95 = false;
        }
        else
        {
            Warned80 = false;
            Warned95 = false;
        }

        UpdatedAt = now;
        return crossed;
    }

    public void MarkWeeklyDigestSent(DateTimeOffset now)
    {
        WeeklyDigestSentAt = now;
        UpdatedAt = now;
    }
}

// FR-20, ADR-0037 決定 10〜13・15・18, [[IADR-0270]] 決定 3: 同期端末とその同期トークン。
// トークンは**ブラウザセッションと別系統**の資格情報であり、平文は発行応答で 1 回だけ返す。
// 保存は SHA-256 ハッシュのみ（漏えい時に原文へ戻せない）。
public class SyncDevice
{
    // ADR-0037 決定 12: 有効期限 30 日。
    public const int TokenLifetimeDays = 30;

    // ADR-0037 決定 18: 期限切れの 7 日前に通知する（当日の追加通知は無い）。
    public const int ExpiryNoticeDays = 7;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string OwnerId { get; private set; } = string.Empty;
    public string DeviceName { get; private set; } = string.Empty;

    // トークンの SHA-256（hex）。平文は保存しない。
    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    // SC-20: 同期状態の表示用（最終同期時刻）。
    public DateTimeOffset? LastSyncAt { get; private set; }

    // FR-22 ③: 期限 7 日前通知の発火記録（1 回だけ送るため。再発行でリセット）。
    public DateTimeOffset? ExpiryNotifiedAt { get; private set; }

    private SyncDevice() { }

    public static SyncDevice Create(string ownerId, string deviceName, string tokenHash,
        DateTimeOffset now) => new()
        {
            OwnerId = ownerId,
            DeviceName = deviceName,
            TokenHash = tokenHash,
            IssuedAt = now,
            ExpiresAt = now.AddDays(TokenLifetimeDays),
        };

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    // ADR-0037 決定 15: **手動再発行**。旧トークンは即座に無効になる（ハッシュが置き換わる）。
    // 自動更新（リフレッシュ）の経路はドメインに存在しない。
    public void Reissue(string newTokenHash, DateTimeOffset now)
    {
        TokenHash = newTokenHash;
        IssuedAt = now;
        ExpiresAt = now.AddDays(TokenLifetimeDays);
        RevokedAt = null;
        ExpiryNotifiedAt = null;
    }

    // ADR-0037 決定 13: 失効（個別・一括の双方から呼ばれる）。冪等。
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public void TouchSync(DateTimeOffset now) => LastSyncAt = now;

    public void MarkExpiryNotified(DateTimeOffset now) => ExpiryNotifiedAt = now;
}
