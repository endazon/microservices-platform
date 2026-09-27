using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.Delete;

// FR-06, UC-03, SC-05（#629）: 削除は**人については管理者限定**（計画の列挙「文書の削除」）。
// FR-06, FR-08, 計画 ADR-0119 決定 2 (#1616): **機械クライアントは自分が `owner` の組織文書に限り**削除できる
// （動的束縛。ロールは足さない）。判定は `UpdateMetadata` と同じ 2 段（入口の門 → 取得後の所有者の判定）。
// 削除の伝播範囲（本文・索引まで）は ADR-0057 のとおりで、経路によらず同じ。
// ADR-0027 / E3a: 削除イベントの発行は Wolverine（IDocumentDeletedPublisher 経由）。
// ADR-0057 決定 1 / [[IADR-0296]]: **削除は本文の実体まで及ぶ。** 台帳から逆引きした
// オブジェクトを**先に**消し、その後で DB 行を消す（順序の根拠は同 IADR 決定 3）。
// オブジェクト削除が失敗すれば例外が出て `SaveChangesAsync` へ到達せず、**行は残る**
// （fail-closed。「消したのに実体が残り、参照も失われた」を作らない）。
internal static class DeleteDocumentEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapDelete("/{id:guid}", async (Guid id, DocumentDbContext db,
            IDocumentDeletedPublisher deletedBus, DocumentObjectPurger purger,
            HttpContext http, DocumentReadAccess reads, CancellationToken ct) =>
        {
            // FR-06, SC-05, ADR-0119 決定 2 (#1616): 運用者だけの人は文書の有無に依らず 403（従前の `AdminOnly` と同じ）。
            if (DocumentManageScope.ForbidUnlessAdminOrMachine(http.User) is { } deleteForbidden)
                return deleteForbidden;

            // FR-19, ADR-0036 D-08, ADR-0119 決定 3 (#1629): 個人資料はこの口の対象外（主体を問わず 404）。
            var doc = await DocumentManageScope.FindManageableAsync(db, id, ct);
            if (doc is null) return Results.NotFound();

            // FR-06, ADR-0119 決定 2, ADR-0056 (#1616): 管理者でなければ、自分が owner の機械クライアントだけが消せる。
            if (await DocumentManageScope.DenyUnlessAdminOrMachineOwnerAsync(http.User, doc, reads, db, ct)
                is { } deleteDenied)
                return deleteDenied;
            await purger.PurgeAsync([id], ct);
            db.Documents.Remove(doc);
            await db.SaveChangesAsync(ct);
            // Issue #88: 削除を下流（Wiki.js 同期・索引・グラフ）へ伝播し、外部システムの実体を撤去する。
            await deletedBus.PublishDeletedAsync(id, DateTimeOffset.UtcNow, ct);
            return Results.NoContent();
        });
    }
}
