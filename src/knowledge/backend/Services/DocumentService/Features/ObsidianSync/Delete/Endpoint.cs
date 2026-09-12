using DocumentService.Infrastructure.Persistence;
// #1446: 方向・結果・失敗理由の値集合は契約が持つ（後段と BFF と画面で同じ値を使う）。
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.ObsidianSync.Delete;

// FR-20, ADR-0037 決定 5: Obsidian 側の削除はサーバ側で**論理削除**とする（90 日保管）。
// 冪等（削除済みへの再削除は期限を延ばさない）。
internal static class DeleteNoteEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/notes/{id:guid}/delete", async (Guid id, HttpContext http,
            DocumentDbContext db, IAuditLogger audit, CancellationToken ct) =>
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

            note.SoftDelete(now);
            device.TouchSync(now);
            // ADR-0099 決定 5 (#1446): 削除の内訳は `deleted=1`（方向は端末 → サーバ＝push）。
            SyncAuditRecorder.Success(db, audit, device.OwnerId, device, SyncOps.Delete,
                added: 0, updated: 0, deleted: 1, now, extra: "count=1");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { deletedAt = note.DeletedAt, purgeAt = note.PurgeAt });
        });
    }
}
