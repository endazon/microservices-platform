using Knowledge.Contracts.Dtos;

namespace DocumentService.Domain;

// FR-19, UC-11, SC-19, 計画 ADR-0036 D-08, 08_data-egress-policy §経路 1, [[IADR-0610]] (#2046):
// **緊急アクセスの監査ログ 1 行**（貯蔵を持つ監査ログ。保持 3 年）。
//
// 記録項目は D-08 の 6 項目（発動日時 / 発動者 / 対象資料の識別子 / 理由 / 操作種別 / 終了日時）に、
// 行の日時・操作した人・指名された管理者・閲覧の対象（属性か本文か）を足したもの。
// 操作種別は planning#841 の裁定 5 により **発動・終了・有効中の管理者の閲覧（1 件ずつ）** の 3 つ。
//
// 🔴 **資料の本文・題名を持つ列を作らない**（D-08「本文は記録しない」を型で守る。`SyncAuditEntry` と同じ作法）。
// 🔴 **FK を張らない**（資料・発動記録が消えても行は保持期間残る）。削るのは定期処理の保持期限だけ。
// 🔴 **終了日時は記録した時点の値**である（発動の行は発動＋15 分。本人が後で終えた結果は終了の行が持つ）。
//   読み取りの口は発動記録の現在の終了日時で上書きして返す（記録が残っている間）。
public class BreakGlassAuditEntry
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid GrantId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    // `BreakGlassOperations` の値。
    public string Operation { get; private set; } = string.Empty;

    // 操作した利用者（発動・終了は所有者本人、閲覧は指名された管理者）。
    public string ActorId { get; private set; } = string.Empty;

    public Guid DocumentId { get; private set; }

    // 発動者（所有者本人）。本人の読み取りはこの列で絞る。
    public string OwnerId { get; private set; } = string.Empty;

    public string NomineeId { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset ActivatedAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    // 閲覧の行だけ（`BreakGlassViewTargets`）。
    public string? ViewTarget { get; private set; }

    private BreakGlassAuditEntry() { }

    public static BreakGlassAuditEntry Activated(BreakGlassGrant grant)
        => Of(grant, BreakGlassOperations.Activate, grant.OwnerId, grant.ActivatedAt, viewTarget: null);

    public static BreakGlassAuditEntry Ended(BreakGlassGrant grant, DateTimeOffset now)
        => Of(grant, BreakGlassOperations.End, grant.OwnerId, now, viewTarget: null);

    public static BreakGlassAuditEntry Viewed(BreakGlassGrant grant, string viewTarget, DateTimeOffset now)
        => Of(grant, BreakGlassOperations.View, grant.NomineeId, now, viewTarget);

    private static BreakGlassAuditEntry Of(BreakGlassGrant grant, string operation, string actor,
        DateTimeOffset now, string? viewTarget) => new()
        {
            GrantId = grant.Id,
            OccurredAt = now,
            Operation = operation,
            ActorId = actor,
            DocumentId = grant.DocumentId,
            OwnerId = grant.OwnerId,
            NomineeId = grant.NomineeId,
            Reason = grant.Reason,
            ActivatedAt = grant.ActivatedAt,
            EndsAt = grant.EndsAt,
            ViewTarget = viewTarget,
        };

    // 発動記録が残っていれば、その現在の終了日時（本人の早期終了を反映した値）で返す。
    public BreakGlassAuditEntryDto ToDto(BreakGlassGrant? grant)
        => new(Id, GrantId, OccurredAt, Operation, ActorId, DocumentId, OwnerId, NomineeId, Reason,
            ActivatedAt, grant?.EndsAt ?? EndsAt, ViewTarget);
}
