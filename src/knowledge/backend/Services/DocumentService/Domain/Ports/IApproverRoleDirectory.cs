namespace DocumentService.Domain.Ports;

// FR-05, FR-18, NFR-09, SC-05, 計画 ADR-0088 決定 1, ADR-0063 決定 3, [[IADR-0410]] 追記 2 (#1636):
// **east-west の本文が運んだ承認者が、いま `platform-admin` を持つか**を基盤の利用者名簿へ訊く口。
//
// ■ なぜ本文のロールを使わないのか
//   gRPC のタグの反映は、承認者の realm ロールを本文の `user_roles` で受け取っていた。それは呼び出し元の**主張**であり、
//   計画 ADR-0088 が閉じた「偽の属性を主張する」と同じ型である（中継者が 1 つ侵害されれば任意の利用者を管理者にできる）。
//   IdP を引けるのは認可サービスだけ（[[IADR-0329]] 決定 1）なので、狭い読み口 `UserDirectory/CheckRealmRole` で問う。
//
// ■ 管理者の上書きそのものは残す
//   ADR-0063 決定 3 の②は取り込み文書（`owner=system`）を承認できる唯一の枝である。外すと SC-05 の管理者の承認が壊れる。
public interface IApproverRoleDirectory
{
    /// <summary>
    /// 承認者 1 人が `platform-admin` を持つかを返す。
    /// 🔴 **引けなかった・時間切れ・未構成は <see cref="ApproverAdminState.Unknown"/>**（例外にしない）。
    /// 呼び出し元は Unknown を「管理者ではない」へ畳まない（障害を「書けない」と記録しない）。
    /// </summary>
    Task<ApproverAdminState> GetAdminStateAsync(string username, CancellationToken ct);
}

// FR-18, NFR-09, [[IADR-0410]] 追記 2 (#1636): 承認者が管理者か。
// 🔴 **`Unknown` を 0 に置く** —— 既定値（初期化漏れ・未知の値の写し損ね）が「管理者」へ倒れない。
// 🔴 **bool にしない** —— `false` が「管理者ではない」と「分からなかった」を畳み、障害が 404 に化ける。
public enum ApproverAdminState
{
    /// <summary>判定できない（名簿を読めない・時間切れ・口が未構成）。</summary>
    Unknown = 0,

    /// <summary>名簿に居て、有効で、`platform-admin` を実効で持つ。</summary>
    Admin,

    /// <summary>持たない（名簿に居ない・無効化されている・ロールが無い）。</summary>
    NotAdmin,
}
