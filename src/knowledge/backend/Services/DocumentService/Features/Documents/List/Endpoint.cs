using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents.List;

// FR-06, UC-03: 文書の一覧（更新の新しい順）。
// **ロールで塞がない**（SC-03 の一般利用者の閲覧。機密制御は取得段の ABAC が担う。IADR-0012）。
internal static class ListDocumentsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (DocumentDbContext db) =>
        {
            var names = await TagResolver.NamesAsync(db);
            var docs = await db.Documents
                .OrderByDescending(d => d.UpdatedAt)
                .ToListAsync();
            return Results.Ok(docs.Select(d => DocumentEndpoints.ToDto(d, names)).ToList());
        });
    }
}
