using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.Delete;

// FR-06, UC-03, SC-05（#629）: 削除は**管理者限定**（計画の列挙「文書の削除」）。
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
            CancellationToken ct) =>
        {
            var doc = await db.Documents.FindAsync([id], ct);
            if (doc is null) return Results.NotFound();
            await purger.PurgeAsync([id], ct);
            db.Documents.Remove(doc);
            await db.SaveChangesAsync(ct);
            // Issue #88: 削除を下流（Wiki.js 同期・索引・グラフ）へ伝播し、外部システムの実体を撤去する。
            await deletedBus.PublishDeletedAsync(id, DateTimeOffset.UtcNow, ct);
            return Results.NoContent();
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
