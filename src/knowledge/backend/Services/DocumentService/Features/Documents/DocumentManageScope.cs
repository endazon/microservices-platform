using System.Security.Claims;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Observability;

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
//
// ── FR-06, FR-08, 計画 ADR-0119 決定 2, ADR-0036 D-01・D-07, ADR-0056 (#1616) ──
// **機械クライアントは、自分が `owner` の組織文書に限り、メタデータ更新と削除を行ってよい。** 判定は動的束縛
// `doc.owner ∈ { ${current_user} }` で行い、**ロールは足さない**（書き込みの群の下限 admin / operator はそのまま）。
// 人の利用者には SC-05 の「管理者限定」がそのまま効く（運用者だけの人は従前どおり 403）。
// 個人資料は上の `FindManageableAsync` が先に 404 にするので、この分岐は個人資料に及ばない（ADR-0034 決定 9）。
internal static class DocumentManageScope
{
    public static async Task<Document?> FindManageableAsync(
        DocumentDbContext db, Guid id, CancellationToken ct)
    {
        var doc = await db.Documents.FindAsync([id], ct);
        return doc is null || DocumentScopes.IsPrivateNote(doc.Attributes) ? null : doc;
    }

    // ADR-0119 決定 2: **機械クライアントの主体名 ＝ そのサービスアカウント**（`service-account-<clientId>`）。
    // 人・未認証は null。人か機械かは `MachinePrincipal.IsMachine` ただ 1 つで決める（述語を新設しない）。
    //   腕 A（`preferred_username = service-account-…`）はその名前。
    //   腕 B（利用者名が無くクライアント識別だけがある）は Keycloak の同じ規約で名前を組み立てる ——
    //   同じクライアントが `profile` スコープの有無で別の所有者にならないようにする。
    public static string? MachineSubject(ClaimsPrincipal user)
    {
        if (!MachinePrincipal.IsMachine(user)) return null;

        var name = user.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();

        return MachinePrincipal.ClientIdOf(user) is { } clientId
            ? MachinePrincipal.ServiceAccountUsernamePrefix + clientId
            : null;
    }

    // メタデータ更新・削除の**入口の門**（取得・入力検証より前）。管理者か機械クライアントなら中へ通す。
    // 🔴 **運用者だけの人はここで 403**（`AdminOnly` を積んでいた頃と同じく、文書の有無・本文の中身に依らない）。
    public static IResult? ForbidUnlessAdminOrMachine(ClaimsPrincipal user)
        => user.IsInRole(PlatformAuthPolicies.AdminRole) || MachinePrincipal.IsMachine(user)
            ? null
            : Results.StatusCode(StatusCodes.Status403Forbidden);

    // 取得した（＝組織文書の）文書に対して、管理者でも所有者の機械でもなければ拒否する。
    // 拒否の形は ADR-0056 に従う —— **読めるが書けない文書は 403、読めない文書は 404**。
    // 「読めるか」は読み取りの唯一の判定点（`DocumentReadAccess`。ADR-0119 決定 3）で答える。
    public static async Task<IResult?> DenyUnlessAdminOrMachineOwnerAsync(
        ClaimsPrincipal user, Document doc, DocumentReadAccess reads, DocumentDbContext db, CancellationToken ct)
    {
        if (user.IsInRole(PlatformAuthPolicies.AdminRole)) return null;

        if (MachineSubject(user) is { } subject && DocumentBodyIntake.IsOwnedBy(doc.Attributes, subject))
            return null;

        var sharedWith = await DocumentEndpoints.ResolveSharedWithAsync(db, doc.Id, ct);
        return await reads.CanReadAsync(DocumentReadPrincipal.FromUser(user), doc, sharedWith, ct)
            ? Results.StatusCode(StatusCodes.Status403Forbidden)
            : Results.NotFound();
    }

    // FR-06, FR-19, ADR-0119 決定 2, ADR-0036 §未確定事項 3 (#1616): `owner` の不変性（`PUT` / `PATCH` の両方・主体を問わない）。
    // `doc_scope` の不変性（`DocumentEndpoints.DocScopeChangedProblemOrNull`）の**直後**に置く。
    public static IResult? OwnerChangedProblemOrNull(
        Dictionary<string, string>? incoming, IReadOnlyDictionary<string, string> current)
    {
        var (ok, error) = DocumentBodyIntake.ValidateOwnerUnchanged(incoming, current);
        return ok
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [DocumentBodyIntake.OwnerKey] = [error!]
            });
    }
}
