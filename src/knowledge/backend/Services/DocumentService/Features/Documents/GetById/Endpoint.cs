using DocumentService.Infrastructure.Persistence;

namespace DocumentService.Features.Documents.GetById;

// FR-06, UC-03: 文書 1 件の取得。
internal static class GetDocumentEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}", async (Guid id, DocumentDbContext db) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            return Results.Ok(DocumentEndpoints.ToDto(doc, await TagResolver.NamesAsync(db)));
        });
    }
}
