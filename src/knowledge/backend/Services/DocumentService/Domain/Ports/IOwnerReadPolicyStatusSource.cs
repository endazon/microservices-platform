namespace DocumentService.Domain.Ports;

// FR-05, FR-19, NFR-09, 計画 ADR-0121 決定 2, [[IADR-0481]] (#1665):
// **所有者の読み取りのポリシーの有効な件数を認可サービスへ問う口**（内容の ABAC の門だけが使う）。
//
// 🔴 **戻り値 null は「数えられない」である。** 宛先の未構成・RPC の失敗・時間切れ・s2s トークンの失敗を区別しない
// （門はどれも「開けない」へ倒す＝fail-closed）。原因はアダプタがログへ残す。0 は「無い」である。
public interface IOwnerReadPolicyStatusSource
{
    Task<int?> GetActiveCountAsync(CancellationToken ct);
}
