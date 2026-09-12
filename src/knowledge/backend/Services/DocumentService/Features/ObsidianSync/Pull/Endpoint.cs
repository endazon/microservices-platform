using DocumentService.Infrastructure.Persistence;
// #1446: 方向・結果・失敗理由の値集合は契約が持つ（後段と BFF と画面で同じ値を使う）。
using Knowledge.Contracts.Dtos;
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
            if (note is null)
            {
                // #1446, ADR-0099 決定 6: 失敗も履歴へ残す。**応答（404）は変えない。**
                await SyncAuditRecorder.FailureAsync(db, audit, device.OwnerId, device,
                    SyncOps.Pull, SyncFailureReasons.NotFound, conflicted: 0, now, ct);
                return Results.NotFound();
            }
            var doc = await db.Documents.FindAsync([id], ct);
            if (doc is null)
            {
                await SyncAuditRecorder.FailureAsync(db, audit, device.OwnerId, device,
                    SyncOps.Pull, SyncFailureReasons.NotFound, conflicted: 0, now, ct);
                return Results.NotFound();
            }

            var content = doc.MarkdownUri is null
                ? string.Empty
                : await storage.GetTextAsync(doc.MarkdownUri, ct);

            device.TouchSync(now);
            // ADR-0099 決定 5 (#1446): pull の内訳は `updated=1` である ——
            // 端末が 1 件受け取ったという事実であり、**追加か更新かはサーバが知らない**。
            SyncAuditRecorder.Success(db, audit, device.OwnerId, device, SyncOps.Pull,
                added: 0, updated: 1, deleted: 0, now, extra: "count=1");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new PullNoteResponse(note.DocumentId, doc.Title, note.VaultPath,
                doc.Version, note.ContentHash, note.IsDeleted, content));
        });
    }
}
