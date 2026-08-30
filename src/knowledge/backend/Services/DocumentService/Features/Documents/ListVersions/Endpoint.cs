using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents.ListVersions;

// FR-06, UC-03: 版履歴一覧（新しい順）。
internal static class ListDocumentVersionsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/versions", async (Guid id, DocumentDbContext db) =>
        {
            var exists = await db.Documents.AnyAsync(d => d.Id == id);
            if (!exists) return Results.NotFound();

            var names = await TagResolver.NamesAsync(db);
            var versions = await db.DocumentVersions
                .Where(v => v.DocumentId == id)
                .OrderByDescending(v => v.Version)
                .ToListAsync();
            return Results.Ok(versions.Select(v => DocumentEndpoints.ToVersionDto(v, names)).ToList());
        });
    }
}
