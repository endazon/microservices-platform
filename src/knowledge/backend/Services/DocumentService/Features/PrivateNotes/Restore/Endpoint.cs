using DocumentService.Features.Documents;
using Microsoft.EntityFrameworkCore;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;

namespace DocumentService.Features.PrivateNotes.Restore;

// FR-19: 復元（90 日以内。purge 済みは行が無く 404 になる＝復元不可）。
// 🔴 FR-19, ADR-0142 決定 4, IADR-0636 決定 1 (#2138): **復元で元の露出のまま載せ直す。** 削除と同じ門を通す —— 台帳はもう削除中でないので、
// 台帳の露出のまま発行される（1 つでも ON なら索引・グラフへ戻り、3 つとも OFF なら何も出さない）。
internal static class RestorePrivateNoteEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/restore", async (Guid id, HttpContext http, DocumentDbContext db,
            IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();
            var note = await PrivateNoteEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (note is null) return Results.NotFound();
            if (!note.IsDeleted)
                return Results.Conflict(new { error = "not_deleted" });

            var now = DateTimeOffset.UtcNow;
            note.Restore(now);
            // FR-02, [[IADR-0597]] / IADR-0637 (#2150): 発行の連番は並行トークンである。読んだ後に別の発行が確定していれば確定ごと負ける
            // （削除の印も残らない）。#2103 と同じ 409 `version_conflict` を返す（500 にしない）。
            try
            {
                await PrivateNoteEndpoints.SaveDeletionStateAsync(db, bus, id, changed: true, ct);
            }
            catch (DbUpdateException ex) when (DocumentVersionConflict.Is(ex))
            {
                return await DocumentVersionConflict.AfterLostSaveAsync(db, id, expectedVersion: null, ct);
            }
            var doc = await db.Documents.FindAsync([id], ct);
            // #1441: 復元後の同期状態は「削除済みだから対象外」ではなくなる —— 導出をやり直す。
            var enrichment = await PrivateNoteEnrichment.LoadAsync(db, owner, [id], now, ct);
            return Results.Ok(enrichment.ToDto(note, doc));
        });
    }
}
