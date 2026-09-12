using Knowledge.Contracts.Dtos;

namespace DocumentService.Domain;

// FR-20, UC-11, SC-20 主要素 6, ADR-0037 決定 9, ADR-0099, #1446（planning#618 の裁定）:
// 同期の監査ログ 1 行（**貯蔵を持つ監査ログ**）。
//
// ■ なぜ表を作るのか（ADR-0099 決定 1 との関係）
//   決定 1 は「供給元は既存の同期監査ログであり、専用の履歴ストアを持たない」と定めた。ところが
//   実装の `IAuditLogger.Record` は**構造化ログへ書くだけ**で貯蔵が無く、決定 2〜4（本人へ読ませる・
//   3 年保持・50 件表示）はそのままでは成立しない。**専用の履歴ストアを別に起こすのではなく、
//   監査ログそのものへ貯蔵を与える**形で決定 1 に沿う（`SyncAuditRecorder` は 1 回の書き込みで
//   この行と `audit.Record(...)` の両方を出す ——「同じ記録の 2 つの出口」である）。
//
// ■ 🔴 **資料を指す列を持たない**（ADR-0099 決定 5 を型で守る）
//   `DocumentId` も資料タイトルも `VaultPath` も**列として存在しない**。決定 5 は行の内容を
//   「実行日時・端末名・方向・件数の内訳・結果・失敗理由」に限っており、題名は完全削除
//   （ADR-0096）のあとも 3 年残ってはならず、第三者への開放（決定 2 で未確定）が入った瞬間に
//   題名が渡ってしまう。**規約で禁じるのではなく、書ける場所を作らない。**
//   （`VaultPath` は実質的に題名である —— ADR-0037 決定 3 が監査への記録を禁じたのと同じ理由。）
//
// ■ 🔴 **端末名は記録時点の写しである**（決定 5「端末名。識別子は出さない」）
//   `DeviceId` は内部の突合用に持つが**画面へは出さない**（`SyncHistoryEntryDto` に項目が無い）。
//   端末を失効・削除しても行はそのまま読める —— 監査ログだから FK を張らない（下の注記）。
//
// ■ 🔴 **FK を張らない**
//   端末（`SyncDevice`）・資料（`PrivateNote`）が消えても行は残る。連動削除すると
//   「端末を消して履歴を消す」が成立し、監査ログとして意味を失う。保持の期限は 3 年ただ 1 つ
//   （`RetentionYears`。定期処理 ⑦ が削る）。
public class SyncAuditEntry
{
    // ADR-0099 決定 3: 保持は 3 年。**削除の実体は定期処理 ⑦**（`PurgeSyncAuditAsync`）であり、
    // 表示件数（決定 4 の 50 件）とは別の値である（混ぜない）。
    public const int RetentionYears = 3;

    public Guid Id { get; private set; } = Guid.NewGuid();

    // 記録の主体（同期トークンの所有者）。一覧は本人の行だけを引く（決定 2）。
    public string OwnerId { get; private set; } = string.Empty;

    // 突合用の端末 ID。**画面へは出さない**（決定 5）。
    public Guid DeviceId { get; private set; }

    // 記録時点の端末名の写し（端末が消えても行は読める）。
    public string DeviceName { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    // `SyncDirections` の値（`push` / `pull`）。
    public string Direction { get; private set; } = string.Empty;

    // 1 回の同期操作で動いた件数の内訳（現行プロトコルは 1 操作 1 資料）。
    public int Added { get; private set; }
    public int Updated { get; private set; }
    public int Deleted { get; private set; }
    public int Conflicted { get; private set; }

    // `SyncOutcomes` の値（`success` / `failure`）。**失敗も記録する**（決定 6）。
    public string Outcome { get; private set; } = string.Empty;

    // `SyncFailureReasons` のコード。成功なら null。**文言は画面が持つ**（記録はコードだけ）。
    public string? FailureReason { get; private set; }

    private SyncAuditEntry() { }

    // ADR-0099 決定 5・6: 成功の記録。内訳は呼び出し元（各同期経路）が決める。
    public static SyncAuditEntry Success(string ownerId, Guid deviceId, string deviceName,
        string direction, int added, int updated, int deleted, DateTimeOffset now) => new()
        {
            OwnerId = ownerId,
            DeviceId = deviceId,
            DeviceName = deviceName,
            OccurredAt = now,
            Direction = direction,
            Added = added,
            Updated = updated,
            Deleted = deleted,
            Conflicted = 0,
            Outcome = SyncOutcomes.Success,
            FailureReason = null,
        };

    // ADR-0099 決定 6: 失敗の記録。**理由はコード**（`SyncFailureReasons`）で持つ。
    // 競合は `conflicted` に数える（件数の内訳であり、結果は `failure` である）。
    public static SyncAuditEntry Failure(string ownerId, Guid deviceId, string deviceName,
        string direction, string failureReason, int conflicted, DateTimeOffset now) => new()
        {
            OwnerId = ownerId,
            DeviceId = deviceId,
            DeviceName = deviceName,
            OccurredAt = now,
            Direction = direction,
            Added = 0,
            Updated = 0,
            Deleted = 0,
            Conflicted = conflicted,
            Outcome = SyncOutcomes.Failure,
            FailureReason = failureReason,
        };

    // SC-20 主要素 6: 画面へ出す形（`Knowledge.Contracts`）。**`DeviceId` は載せない**（決定 5）。
    public SyncHistoryEntryDto ToDto()
        => new(Id, OccurredAt, DeviceName, Direction, Added, Updated, Deleted, Conflicted,
            Outcome, FailureReason);
}
