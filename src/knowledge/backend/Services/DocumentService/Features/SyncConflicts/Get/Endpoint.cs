using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.SyncConflicts.Get;

// FR-20, SC-20 主要素 5, #1442: 競合の詳細（2 ペイン差分の材料）。
//
// **他人の競合・不在・解決済みはいずれも 404** —— 解決済みを 200 で返すと、画面が
// 「まだ選べる」と見せてしまう（選んでも 409 になる）。
internal static class GetSyncConflictEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, DocumentDbContext db,
            IObjectStorageClient storage, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();

            var conflict = await SyncConflictEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (conflict is null || conflict.IsResolved) return Results.NotFound();

            var (docs, notes, devices) = await SyncConflictEndpoints.LoadLabelsAsync(db,
                [conflict.DocumentId], [conflict.DeviceId], ct);
            var doc = docs.GetValueOrDefault(conflict.DocumentId);

            // ローカル版は競合の記録時に保存したもの、サーバ版は現在の本文である。
            // **どちらも取り出せないことがあり得る**（未配備の縮退ストレージ・削除済み）ため、
            // 引けなければ空文字で返す —— 差分は描けなくても、競合の存在と 3 択は示せる。
            var localContent = await ReadAsync(storage, conflict.LocalContentUri, ct);
            var serverContent = await ReadAsync(storage, doc?.MarkdownUri, ct);

            var summary = SyncConflictEndpoints.ToSummary(conflict, doc?.Title ?? string.Empty,
                notes.GetValueOrDefault(conflict.DocumentId)?.VaultPath ?? string.Empty,
                devices.GetValueOrDefault(conflict.DeviceId)?.DeviceName ?? string.Empty);

            return Results.Ok(new SyncConflictDetailDto(summary.Id, summary.NoteId, summary.Title,
                summary.VaultPath, summary.DetectedAt, summary.DeviceId, summary.DeviceName,
                summary.LocalBaseVersion, summary.ServerVersion, localContent, serverContent));
        });
    }

    internal static async Task<string> ReadAsync(IObjectStorageClient storage, string? uri,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(uri)) return string.Empty;
        try
        {
            return await storage.GetTextAsync(uri, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
