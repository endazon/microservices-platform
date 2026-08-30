using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.ObsidianSync.Pull;

// FR-20: pull（本文の取得）。個人資料の本文が端末へ出る egress の実行点であり、
// 実行記録を監査ログへ残す（許容条件 4。タイトル・内容は記録しない）。
internal static class PullNoteEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/notes/{id:guid}", async (Guid id, HttpContext http, DocumentDbContext db,
            IObjectStorageClient storage, IAuditLogger audit, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = await ObsidianSyncEndpoints.ResolveDeviceAsync(http, db, now, ct);
            if (device is null) return Results.Unauthorized();

            var note = await ObsidianSyncEndpoints.FindOwnedAsync(db, device.OwnerId, id, ct);
            if (note is null) return Results.NotFound();
            var doc = await db.Documents.FindAsync([id], ct);
            if (doc is null) return Results.NotFound();

            var content = doc.MarkdownUri is null
                ? string.Empty
                : await storage.GetTextAsync(doc.MarkdownUri, ct);

            device.TouchSync(now);
            await db.SaveChangesAsync(ct);
            audit.Record("private-note.sync.pull", device.OwnerId, "granted",
                $"device={device.Id} count=1");
            return Results.Ok(new PullNoteResponse(note.DocumentId, doc.Title, note.VaultPath,
                doc.Version, note.ContentHash, note.IsDeleted, content));
        });
    }
}
