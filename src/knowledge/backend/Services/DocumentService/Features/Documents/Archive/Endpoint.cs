using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.Archive;

// FR-06, UC-03, Issue #88: 文書をアーカイブ（非公開化）する。下流の Wiki.js 同期が
// status=archived を受けてページを非公開化・メタデータ Archived 化する。
// FR-06, UC-03, SC-05（#629）: アーカイブは**管理者限定**。公開と同じ基準で分類した
// （作業仕様書 §判断 1）。**アーカイブは (a) すら満たさない** —— 下流の Wiki.js 同期が
// ページを非公開化するため、**可視性を落とす**。「既存データを壊さない」とは言い切れない。
internal static class ArchiveDocumentEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost("/{id:guid}/archive", async (Guid id, DocumentDbContext db,
            IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            doc.Archive();
            await db.SaveChangesAsync();
            var names = await TagResolver.NamesAsync(db);
            await DocumentEndpoints.PublishUpdatedIfIndexableAsync(bus, db, doc, names, ct);
            return Results.Ok(DocumentEndpoints.ToDto(doc, names));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
