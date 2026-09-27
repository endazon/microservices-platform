using Platform.Shared.Contracts.Dtos;

namespace DocumentService.Domain.Ports;

// FR-05, FR-19, NFR-09, 計画 ADR-0119 決定 3, ADR-0036 D-06, ADR-0098 決定 1 (#1614):
// **利用者の読み取りの許可を認可サービスへ問う口。**
//
// ■ 何に使うか（#1614）
//   （門が閉じている間は）個人資料の**グループの共有先**の判定だけに使う。所属は認可サービスが IdP から引く
//   （トークンの `groups` は読まない —— [[IADR-0447]] と同じ理由）。所有者・利用者の共有先は
//   DocumentService の台帳だけで決まるので、この口を呼ばない。
//
// ■ ［2026-09-28 更新 / #1615］内容の ABAC の門が開いたときは、**読み取りの判定のすべて**（組織文書の機密・部門・ライフサイクル、
//   個人資料の所有者・利用者共有・グループ共有）にこの口の同じ分岐を当てる（計画 ADR-0121 決定 5）。機械の主体は
//   そのサービスアカウント名で問う。判定の点（`DocumentReadAccess`）も問い合わせの口も増やしていない。
//
// 🔴 **戻り値 null は「読めるものは無い」である。** 許可なし・引けなかった・時間切れ・未構成を区別しない
// （呼び出し側はどれも「その資料は読めない」へ倒す＝fail-closed）。原因はアダプタがログへ残す。
public interface IDocumentReadScopeSource
{
    /// <summary>
    /// 利用者 1 人の <c>read</c> の許可を、**分岐の並び**（分岐内 AND・分岐間 OR）で返す。
    /// 分岐を持たない応答は従来の連言 1 つを 1 分岐として返す。**許可が無い・引けないときは null。**
    /// </summary>
    Task<IReadOnlyList<IReadOnlyList<AttributeFilter>>?> ResolveReadBranchesAsync(
        string userId, CancellationToken ct);
}
