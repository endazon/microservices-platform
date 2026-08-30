using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.PrivateNotes.SoftDelete;

// FR-19, ADR-0037 決定 5・19: 論理削除（90 日間は復元可）。**容量は空かない**
// （capacityFreed=false を応答で明示し、SC-19 の確認文言の根拠にする。決定 20）。
internal static class SoftDeletePrivateNoteEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, DocumentDbContext db,
            CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();
            var note = await PrivateNoteEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (note is null) return Results.NotFound();

            note.SoftDelete(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            // 決定 19・20: 論理削除しても容量は空かない（利用者へ伝える事実の機械可読な形）。
            // **契約型で返す**（#451-a）—— 匿名型だと BFF・画面・openapi のどれとも突き合わない。
            return Results.Ok(new PrivateNoteDeletedResponse(note.DeletedAt, note.PurgeAt,
                CapacityFreed: false));
        });
    }
}
