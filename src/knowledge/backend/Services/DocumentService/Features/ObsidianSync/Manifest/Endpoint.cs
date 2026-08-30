using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.ObsidianSync.Manifest;

// FR-20: マニフェスト（同期対象の一覧）。削除済みも deleted=true で返し、
// サーバ側の削除をプラグインが検知できるようにする（決定 14: KB が正）。
internal static class SyncManifestEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/manifest", async (HttpContext http, DocumentDbContext db,
            CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = await ObsidianSyncEndpoints.ResolveDeviceAsync(http, db, now, ct);
            if (device is null) return Results.Unauthorized();

            var notes = await db.PrivateNotes.Where(n => n.OwnerId == device.OwnerId)
                .OrderBy(n => n.VaultPath).ToListAsync(ct);
            var docIds = notes.Select(n => n.DocumentId).ToList();
            var docs = await db.Documents.Where(d => docIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, ct);

            device.TouchSync(now);
            await db.SaveChangesAsync(ct);

            return Results.Ok(notes.Select(n => new SyncManifestEntry(
                n.DocumentId,
                docs.GetValueOrDefault(n.DocumentId)?.Title ?? string.Empty,
                n.VaultPath,
                docs.GetValueOrDefault(n.DocumentId)?.Version ?? 0,
                n.ContentHash,
                n.IsDeleted,
                n.UpdatedAt)).ToList());
        });
    }
}
