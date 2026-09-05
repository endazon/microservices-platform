using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.Publish;

// FR-06, UC-03, SC-05: 文書を公開する。アーカイブ済みからの再公開は不正遷移として 409 で拒否する。
// FR-06, UC-03, SC-05（#629）: 公開は**管理者限定**。
//
// **計画の破壊的操作の列挙に名前が無い**ため、planning#299 が新設した基準を当てはめた。
// 同基準の例外（運用者へ開く）は **2 条件を同時に満たすとき**であり、
// 唯一の適用例である手動同期（SC-06）は (a) 既存データを壊さない ＋
// (b) **運用者が SC-10 で異常に気づいたその場で一次対応できる**の両方を満たしていた。
// **公開は (b) を満たさない** —— 異常への一次対応ではなく、公開範囲を決める統制行為である。
// **計画が名指しした例外は手動同期ただ 1 つ**なので、名指しの無い操作の既定は一般則
// （破壊的操作は管理者限定）に従う。判断の全文は作業仕様書 §判断 1。
internal static class PublishDocumentEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost("/{id:guid}/publish", async (Guid id, DocumentDbContext db,
            IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            if (!doc.CanPublish)
                return Results.Conflict(new
                {
                    error = "invalid_transition",
                    from = doc.Status,
                    to = DocumentStatus.Published
                });
            doc.Publish();
            await db.SaveChangesAsync();
            var names = await TagResolver.NamesAsync(db);
            await DocumentEndpoints.PublishUpdatedIfIndexableAsync(bus, db, doc, names, ct);
            return Results.Ok(DocumentEndpoints.ToDto(doc, names));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
