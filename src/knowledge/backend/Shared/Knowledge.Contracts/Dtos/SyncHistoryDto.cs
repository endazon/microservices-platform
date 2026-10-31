namespace Knowledge.Contracts.Dtos;

// FR-20, UC-11, SC-20 主要素 6, ADR-0037 決定 9, ADR-0099, planning#618: 同期履歴の契約 DTO。
//
// 供給元は**同期の監査ログ**（ADR-0037 決定 9「誰が・いつ・何件」）を本人スコープで読んだものであり、
// 専用の履歴ストアは持たない（ADR-0099 決定 1）。**読めるのは本人の記録だけ**（決定 2）。
//
// 🔴 **資料のタイトル・Vault のパス・資料 ID を運ぶ項目を持たない**（決定 5。完全削除〔ADR-0096〕の
// あとも題名が 3 年間残らない／第三者への開放が未確定〔決定 2〕であり、開いた瞬間に題名が渡らない）。
// SC-20 §未確定「監査ログに資料 ID を記録するか」も未決のままなので、ID も載せない。
// 🔴 **端末名を運び、端末 ID は運ばない**（決定 5「端末名（識別子は出さない）」）。端末名は記録時点の
// 写しであり、端末の失効・削除後も行はそのまま読める。

// SC-20 主要素 6: 同期履歴 1 行。内訳は 1 回の同期操作で動いた件数（現行プロトコルは 1 操作 1 資料）。
public record SyncHistoryEntryDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string DeviceName,
    string Direction,
    int Added,
    int Updated,
    int Deleted,
    int Conflicted,
    string Outcome,
    string? FailureReason,
    // FR-20, 計画 ADR-0145 決定 3, [[IADR-0667]] (#2378): **停止期間ごとにまとめた拒否の行だけ**が持つ（他の行は null）。
    // 拒んだ要求の数と、最初・最後の拒否の時刻。行の `OccurredAt` は最初の拒否の時刻、方向は空、内訳は 0 である。
    int? RejectedCount = null,
    DateTimeOffset? FirstRejectedAt = null,
    DateTimeOffset? LastRejectedAt = null);

// ADR-0099 決定 5: 方向（送信＝端末→サーバ／受信＝サーバ→端末）。
public static class SyncDirections
{
    public const string Push = "push";
    public const string Pull = "pull";
}

// ADR-0099 決定 5・6: 結果。失敗も記録する。
public static class SyncOutcomes
{
    public const string Success = "success";
    public const string Failure = "failure";
}

// ADR-0099 決定 5・6: 失敗理由のコード（契約の値集合。**文言は画面が持つ** —— 「利用者が次に何をすれば
// よいか分かる文言」は表示側の責務であり、記録はコードだけを持つ）。
// 競合・削除済み・パス衝突の 3 経路（決定 6 が名指しした）に、後段が既に拒む 4 経路を足す。
public static class SyncFailureReasons
{
    public const string VersionConflict = "version_conflict";
    public const string Deleted = "deleted";
    public const string PathConflict = "path_conflict";
    public const string QuotaExceeded = "quota_exceeded";
    public const string BodyTooLarge = "body_too_large";
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";

    // FR-20, 計画 ADR-0145 決定 2・3, [[IADR-0667]] (#2378): 一時停止中の拒否。**同期の口の応答（423 の `error`）と同じ値**を使う
    // （プラグインと試験は状態コードではなくこの値で「停止中」を判定する）。
    public const string Paused = "sync_paused";
}
