using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents.ListShares;

// FR-20: 共有先の一覧（所有者のみ。共有の管理は所有者の裁量に属する）。
internal static class ListDocumentSharesEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid id, DocumentDbContext db, HttpContext http) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            if (!DocumentBodyIntake.CanWrite(doc.Attributes, http.User.Identity?.Name))
                return Results.NotFound();

            var shares = await db.DocumentShares
                .Where(s => s.DocumentId == id)
                .OrderBy(s => s.CreatedAt)
                .Select(s => new DocumentShareDto(s.SubjectType, s.SubjectId, s.GrantedBy, s.CreatedAt))
                .ToListAsync();
            return Results.Ok(shares);
        });
    }
}
