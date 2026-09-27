using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.Documents;

// FR-06, FR-19, UC-03, SC-05, NFR-09, 計画 ADR-0036 D-08, ADR-0119 決定 3, ADR-0056 決定 1, [[IADR-0044]] (#1629):
// **管理の書き込み口（`PUT /documents/{id}`・`PATCH /{id}/metadata`・`POST /{id}/publish`・`POST /{id}/archive`・
// `DELETE /{id}`）が作用してよい文書を引く唯一の点。**
//
// 🔴 **個人資料は、この 5 口の対象外である。主体を問わず「無い」と同じ 404 に畳む。**
// ADR-0036 D-08 は「管理者・運用者は平時、非公開の個人資料を**一切閲覧できない**」と定め、ADR-0119 決定 3 は
// 個人資料を「所有者と共有先にだけ返す（管理者を含め、他の主体には返さない）」とした。5 口は `AdminOnly` で
// 守られているが、ロールは所有者の束縛ではない —— 従前は管理者（人・機械の `abac-seeder`）が他人の個人資料を
// 書き換え・公開・保管・削除でき、応答に完全な DTO（表題・owner・共有先）が返っていた。`PUT` は属性を全置換
// するので `owner` を自分へ書き換え、そのあと読み取りの口から読むことさえできた。
//
// **除外は所有者を問わず一律である**（BFF の `IsManageable` と同じ形。所有者の分岐を持ち込まない）。
// 個人資料を扱う口は FR-19 の所有者の経路（`/private-notes/*`・Obsidian 同期・`PUT …/body`・共有台帳）であり、
// 所有権は `PrivateNote.OwnerId` と属性 `owner` の 2 か所にある。管理の口で所有者を許すと、`PUT` の属性全置換で
// 2 つが食い違う・ごみ箱を経ずに消える、の形が所有者自身の手でも作れてしまう。
//
// 🔴 **拒否は 404 であり、403 にしない**（ADR-0056 決定 1・[[IADR-0277]]）。403 は文書 ID の総当たりで
// 個人資料の実在を明かす。判定は集合帰属（`DocumentScopes.IsPrivateNote`。キー欠落は組織文書）で書く。
//
// 🔴 **5 口は必ずここを通す。`db.Documents.FindAsync` を口の中へ書き戻さない** ——
// 1 口だけ戻すと、その口から他人の個人資料へ作用できる（`AdminWritePrivateNoteScopeTests` が 5 口を列挙して止める）。
internal static class DocumentManageScope
{
    public static async Task<Document?> FindManageableAsync(
        DocumentDbContext db, Guid id, CancellationToken ct)
    {
        var doc = await db.Documents.FindAsync([id], ct);
        return doc is null || DocumentScopes.IsPrivateNote(doc.Attributes) ? null : doc;
    }
}
