using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents.GetVersion;

// FR-06, UC-03: 特定版の取得。
internal static class GetDocumentVersionEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version,
            DocumentDbContext db) =>
        {
            var snapshot = await db.DocumentVersions
                .FirstOrDefaultAsync(v => v.DocumentId == id && v.Version == version);
            if (snapshot is null) return Results.NotFound();
            return Results.Ok(
                DocumentEndpoints.ToVersionDto(snapshot, await TagResolver.NamesAsync(db)));
        });
    }
}
