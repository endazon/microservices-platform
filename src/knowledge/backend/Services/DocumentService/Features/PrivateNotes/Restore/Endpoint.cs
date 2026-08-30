using DocumentService.Infrastructure.Persistence;

namespace DocumentService.Features.PrivateNotes.Restore;

// FR-19: 復元（90 日以内。purge 済みは行が無く 404 になる＝復元不可）。
internal static class RestorePrivateNoteEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/restore", async (Guid id, HttpContext http, DocumentDbContext db,
            CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();
            var note = await PrivateNoteEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (note is null) return Results.NotFound();
            if (!note.IsDeleted)
                return Results.Conflict(new { error = "not_deleted" });

            note.Restore(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            var doc = await db.Documents.FindAsync([id], ct);
            return Results.Ok(PrivateNoteEndpoints.ToDto(note, doc));
        });
    }
}
