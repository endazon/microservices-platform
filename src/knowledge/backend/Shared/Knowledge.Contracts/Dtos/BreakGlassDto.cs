namespace Knowledge.Contracts.Dtos;

// FR-19, UC-11, SC-19, SC-03, 計画 ADR-0036 D-08, 08_data-egress-policy §経路 1, [[IADR-0610]] (#2046):
// 緊急アクセス（Break-glass）の契約。**本人同意型の管理者の閲覧**である（planning#841 の裁定 2026-10-23）。
//
// ■ 何が開くか
//   所有者本人が、自分の個人資料 1 件について、理由を書き、`platform-admin` のうち 1 人を指名して発動する。
//   有効な間だけ、**指名された 1 人**がその 1 件を**閲覧**できる（編集・削除・共有の変更・ダウンロードは増えない）。
//   有効時間は発動から 15 分で延長しない。本人は 15 分より前に終えられる（終了日時は本人の終了か発動＋15 分の早いほう）。
//
// ■ 🔴 有効性はこの DTO で判定しない
//   `Active` / `EndsAt` は応答の時点の写しである。閲覧の可否は文書サービスが**要求ごとにサーバ側の発動記録**で決める
//   （ADR-0036 D-08）。呼び出し側（BFF・画面）はこの値を許可の根拠に使わない（残り時間の表示にだけ使う）。
//
// BFF（2 本目）と画面（3 本目）が同じ形を配るため、定義はここ 1 つだけに置く（`PrivateNoteDto` と同じ切り分け）。

/// <summary>発動の要求（所有者本人だけが送れる）。</summary>
/// <param name="Reason">理由（必須。空白だけは不可。最大 <see cref="BreakGlassPolicy.ReasonMaxLength"/> 文字）。</param>
/// <param name="NomineeUserId">閲覧を許す管理者 1 人の利用者名（`preferred_username`）。`platform-admin` を持つこと。</param>
public record ActivateBreakGlassRequest(string? Reason, string? NomineeUserId);

/// <summary>発動記録 1 件（所有者本人へ返す形）。</summary>
/// <param name="Id">発動記録の識別子。</param>
/// <param name="DocumentId">対象の資料（1 件）。</param>
/// <param name="NomineeUserId">指名された管理者。</param>
/// <param name="Reason">発動の理由。</param>
/// <param name="ActivatedAt">発動日時。</param>
/// <param name="ExpiresAt">発動＋15 分（延長しない）。</param>
/// <param name="EndedAt">本人が終えた日時（終えていなければ null）。</param>
/// <param name="EndsAt">終了日時（本人の終了か発動＋15 分の早いほう）。</param>
/// <param name="Active">応答の時点で有効か（表示用。許可の根拠にしない）。</param>
public record BreakGlassGrantDto(
    Guid Id,
    Guid DocumentId,
    string NomineeUserId,
    string Reason,
    DateTimeOffset ActivatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset EndsAt,
    bool Active);

/// <summary>資料 1 件の緊急アクセスの状態。有効な発動が無ければ <paramref name="Active"/> は null。</summary>
public record BreakGlassStatusResponse(BreakGlassGrantDto? Active);

/// <summary>
/// 監査ログ 1 行（発動・終了・有効中の閲覧）。🔴 **資料の本文・題名は運ばない**（ADR-0036 D-08「本文は記録しない」）。
/// 読めるのは `platform-admin` と所有者本人（本人は自分の資料の行だけ）。
/// </summary>
/// <param name="Id">行の識別子。</param>
/// <param name="GrantId">どの発動記録の行か。</param>
/// <param name="OccurredAt">操作の日時。</param>
/// <param name="Operation">操作種別（<see cref="BreakGlassOperations"/>）。</param>
/// <param name="ActorUserId">操作した利用者（発動・終了は所有者、閲覧は指名された管理者）。</param>
/// <param name="DocumentId">対象資料の識別子。</param>
/// <param name="OwnerUserId">発動者（＝所有者本人）。</param>
/// <param name="NomineeUserId">指名された管理者。</param>
/// <param name="Reason">発動の理由。</param>
/// <param name="ActivatedAt">発動日時。</param>
/// <param name="EndsAt">終了日時（本人の終了か発動＋15 分の早いほう）。</param>
/// <param name="ViewTarget">閲覧の行だけ: 何を読んだか（<see cref="BreakGlassViewTargets"/>）。それ以外は null。</param>
public record BreakGlassAuditEntryDto(
    Guid Id,
    Guid GrantId,
    DateTimeOffset OccurredAt,
    string Operation,
    string ActorUserId,
    Guid DocumentId,
    string OwnerUserId,
    string NomineeUserId,
    string Reason,
    DateTimeOffset ActivatedAt,
    DateTimeOffset EndsAt,
    string? ViewTarget);

/// <summary>監査ログの操作種別（planning#841 の裁定 5: 発動・終了・有効中の閲覧）。</summary>
public static class BreakGlassOperations
{
    public const string Activate = "activate";
    public const string End = "end";
    public const string View = "view";
}

/// <summary>閲覧の行が何を読んだか（文書の属性・現行版の本文）。</summary>
public static class BreakGlassViewTargets
{
    public const string Document = "document";
    public const string Content = "content";
}

/// <summary>緊急アクセスの固定値（ADR-0036 D-08）。値をここ以外へ書き下さない。</summary>
public static class BreakGlassPolicy
{
    /// <summary>有効時間（分）。発動時刻が起点で、アイドルで延長しない。</summary>
    public const int ValidityMinutes = 15;

    /// <summary>理由の最大文字数。</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>監査ログ（と発動記録）の保持年数。</summary>
    public const int RetentionYears = 3;
}
