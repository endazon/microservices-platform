using Microsoft.EntityFrameworkCore;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.PrivateNotes.SoftDelete;

// FR-19, ADR-0037 決定 5・19: 論理削除（90 日間は復元可）。**容量は空かない**
// （capacityFreed=false を応答で明示し、SC-19 の確認文言の根拠にする。決定 20）。
//
// 🔴 FR-19, ADR-0142 決定 1, IADR-0636 決定 1 (#2138): **削除で見つかる面から外す。** 削除の状態が変わったとき、門を通して
// 撤収の形の `DocumentUpdated` を確定と同じトランザクションで出す（露出 3 キーを excluded にした写し。台帳の露出は保つ）。
// 露出が 3 つとも OFF なら門で止まり何も出さない（索引に無いまま）。削除済みへの再削除は何も出さない。
internal static class SoftDeletePrivateNoteEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, DocumentDbContext db,
            IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();
            var note = await PrivateNoteEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (note is null) return Results.NotFound();

            var wasDeleted = note.IsDeleted;
            note.SoftDelete(DateTimeOffset.UtcNow);
            // FR-02, [[IADR-0597]] / IADR-0637 (#2150): 発行の連番は並行トークンである。読んだ後に別の発行が確定していれば確定ごと負ける
            // （削除の印も残らない）。#2103 と同じ 409 `version_conflict` を返す（500 にしない）。
            try
            {
                await PrivateNoteEndpoints.SaveDeletionStateAsync(db, bus, id, changed: !wasDeleted, ct);
            }
            catch (DbUpdateException ex) when (DocumentVersionConflict.Is(ex))
            {
                return await DocumentVersionConflict.AfterLostSaveAsync(db, id, expectedVersion: null, ct);
            }
            // 決定 19・20: 論理削除しても容量は空かない（利用者へ伝える事実の機械可読な形）。
            // **契約型で返す**（#451-a）—— 匿名型だと BFF・画面・openapi のどれとも突き合わない。
            return Results.Ok(new PrivateNoteDeletedResponse(note.DeletedAt, note.PurgeAt,
                CapacityFreed: false));
        });
    }
}
