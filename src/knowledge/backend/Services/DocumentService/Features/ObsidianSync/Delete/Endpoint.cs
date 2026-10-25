using DocumentService.Features.Documents;
using Microsoft.EntityFrameworkCore;
using DocumentService.Domain.Ports;
using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
// #1446: 方向・結果・失敗理由の値集合は契約が持つ（後段と BFF と画面で同じ値を使う）。
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.ObsidianSync.Delete;

// FR-20, ADR-0037 決定 5: Obsidian 側の削除はサーバ側で**論理削除**とする（90 日保管）。
// 冪等（削除済みへの再削除は期限を延ばさない）。
// 🔴 FR-19, ADR-0142 決定 1, IADR-0636 決定 1 (#2138): SC-19 の削除と同じく、削除の状態が変わったときに撤収の形の `DocumentUpdated` を出す。
internal static class DeleteNoteEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/notes/{id:guid}/delete", async (Guid id, HttpContext http,
            DocumentDbContext db, IAuditLogger audit, IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = await ObsidianSyncEndpoints.ResolveDeviceAsync(http, db, now, ct);
            if (device is null) return Results.Unauthorized();

            var note = await ObsidianSyncEndpoints.FindOwnedAsync(db, device.OwnerId, id, ct);
            if (note is null)
            {
                // #1446, ADR-0099 決定 6: 失敗も履歴へ残す。**応答（404）は変えない。**
                await SyncAuditRecorder.FailureAsync(db, audit, device.OwnerId, device,
                    SyncOps.Delete, SyncFailureReasons.NotFound, conflicted: 0, now, ct);
                return Results.NotFound();
            }

            var wasDeleted = note.IsDeleted;
            note.SoftDelete(now);
            device.TouchSync(now);
            // ADR-0099 決定 5 (#1446): 削除の内訳は `deleted=1`（方向は端末 → サーバ＝push）。
            SyncAuditRecorder.Success(db, audit, device.OwnerId, device, SyncOps.Delete,
                added: 0, updated: 0, deleted: 1, now, extra: "count=1");
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
            return Results.Ok(new { deletedAt = note.DeletedAt, purgeAt = note.PurgeAt });
        });
    }
}
