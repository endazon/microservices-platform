using DocumentService.Infrastructure.Persistence;
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
            if (note is null) return Results.NotFound();

            note.SoftDelete(now);
            device.TouchSync(now);
            await db.SaveChangesAsync(ct);
            audit.Record("private-note.sync.delete", device.OwnerId, "granted",
                $"device={device.Id} count=1");
            return Results.Ok(new { deletedAt = note.DeletedAt, purgeAt = note.PurgeAt });
        });
    }
}
