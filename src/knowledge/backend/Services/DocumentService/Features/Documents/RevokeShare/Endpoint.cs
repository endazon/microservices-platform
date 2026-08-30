using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Documents.RevokeShare;

// FR-20, ADR-0036 D-06: 共有の取り消し（所有者のみ。取り消し可——ただし
// 「既に見られた事実」は取り消せない）。
internal static class RevokeDocumentShareEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapDelete("/{subjectType}/{subjectId}", async (Guid id, string subjectType,
            string subjectId, DocumentDbContext db, HttpContext http) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            if (!DocumentBodyIntake.CanWrite(doc.Attributes, http.User.Identity?.Name))
                return Results.NotFound();

            var share = await db.DocumentShares.FirstOrDefaultAsync(s => s.DocumentId == id
                && s.SubjectType == subjectType && s.SubjectId == subjectId);
            if (share is null) return Results.NotFound();

            db.DocumentShares.Remove(share);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
