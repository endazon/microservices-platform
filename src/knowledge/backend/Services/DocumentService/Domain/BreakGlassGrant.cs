using Knowledge.Contracts.Dtos;

namespace DocumentService.Domain;

// FR-19, UC-11, SC-19, 計画 ADR-0036 D-08, 08_data-egress-policy §経路 1, [[IADR-0610]] (#2046):
// **緊急アクセスの発動記録**（サーバ側の記録。閲覧の可否はリクエストごとにこの行で判定する）。
//
// planning#841 の裁定（2026-10-23）による形:
//   - 本人同意型の管理者の閲覧。発動するのは所有者本人だけで、閲覧できるのは本人が指名した `platform-admin` の 1 人だけ。
//   - 対象は資料 1 件（`DocumentId`）。
//   - 有効時間は発動から 15 分（`ExpiresAt`。延長しない）。本人は 15 分より前に終えられる（`EndedAt`）。
//     終了日時は「本人の終了か、発動＋15 分の早いほう」（`EndsAt`）。
//
// 🔴 **延長の口を作らない。** `ExpiresAt` を書き換えるメソッドは無い（D-08「アイドルタイムアウトにしない」）。
// 🔴 **FK を張らない。** 資料が完全削除されても記録は保持期間（3 年）残る —— 監査の根拠を資料の寿命に従わせない
//   （`SyncAuditEntry` と同じ判断）。閲覧の判定は資料側の現在の状態も確かめるので、残った行が閲覧を開くことはない。
public class BreakGlassGrant
{
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(BreakGlassPolicy.ValidityMinutes);

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid DocumentId { get; private set; }

    // 発動者 ＝ 発動時点の所有者本人。
    public string OwnerId { get; private set; } = string.Empty;

    // 閲覧を許された管理者（1 人）。
    public string NomineeId { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset ActivatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    // 本人が 15 分より前に終えた日時。終えていなければ null。
    public DateTimeOffset? EndedAt { get; private set; }

    private BreakGlassGrant() { }

    // 入力の検証は端点が済ませている（理由・指名先の形）。ここは不変条件だけを守る（破れたら例外 ＝ 配線の誤り）。
    public static BreakGlassGrant Activate(Guid documentId, string ownerId, string nomineeId, string reason,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("所有者が空です。", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(nomineeId)) throw new ArgumentException("指名先が空です。", nameof(nomineeId));
        if (string.Equals(ownerId, nomineeId, StringComparison.Ordinal))
            throw new ArgumentException("所有者本人を指名できません。", nameof(nomineeId));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("理由が空です。", nameof(reason));

        return new BreakGlassGrant
        {
            DocumentId = documentId,
            OwnerId = ownerId,
            NomineeId = nomineeId,
            Reason = reason.Trim(),
            ActivatedAt = now,
            ExpiresAt = now + Validity,
        };
    }

    /// <summary>終了日時（本人の終了か、発動＋15 分の早いほう）。</summary>
    public DateTimeOffset EndsAt => EndedAt is { } ended && ended < ExpiresAt ? ended : ExpiresAt;

    /// <summary>
    /// <paramref name="now"/> の時点で有効か（発動以後・終了日時より前）。境界: 終了日時ちょうどは無効。
    /// </summary>
    public bool IsActiveAt(DateTimeOffset now) => now >= ActivatedAt && now < EndsAt;

    /// <summary>本人による早期終了。有効でなければ何もしない（false）。</summary>
    public bool EndByOwner(DateTimeOffset now)
    {
        if (!IsActiveAt(now)) return false;
        EndedAt = now;
        return true;
    }

    public BreakGlassGrantDto ToDto(DateTimeOffset now)
        => new(Id, DocumentId, NomineeId, Reason, ActivatedAt, ExpiresAt, EndedAt, EndsAt, IsActiveAt(now));
}
