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
            IObjectStorageClient storage, ILoggerFactory loggers, CancellationToken ct) =>
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
            // NFR-19, #2141: 取り出せなかったことは `*ContentUnavailable` で空の本文と区別して返し、警告を 1 行残す
            // （従前は空文字に畳んで記録も無く、利用者には「中身が空の版」と見分けが付かなかった）。
            var log = loggers.CreateLogger(typeof(GetSyncConflictEndpoint).FullName!);
            var (localContent, localUnavailable) = await ReadAsync(storage, conflict.LocalContentUri, log, "local", ct);
            var (serverContent, serverUnavailable) = await ReadAsync(storage, doc?.MarkdownUri, log, "server", ct);

            var summary = SyncConflictEndpoints.ToSummary(conflict, doc?.Title ?? string.Empty,
                notes.GetValueOrDefault(conflict.DocumentId)?.VaultPath ?? string.Empty,
                devices.GetValueOrDefault(conflict.DeviceId)?.DeviceName ?? string.Empty);

            return Results.Ok(new SyncConflictDetailDto(summary.Id, summary.NoteId, summary.Title,
                summary.VaultPath, summary.DetectedAt, summary.DeviceId, summary.DeviceName,
                summary.LocalBaseVersion, summary.ServerVersion, localContent, serverContent,
                localUnavailable, serverUnavailable));
        });
    }

    /// <summary>本文を取り出す。参照が無いときは空（取り出せなかったのではない）。取り出しが失敗したときは空と <c>Unavailable=true</c>。</summary>
    internal static async Task<(string Content, bool Unavailable)> ReadAsync(IObjectStorageClient storage, string? uri,
        ILogger log, string side, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(uri)) return (string.Empty, false);
        try
        {
            return (await storage.GetTextAsync(uri, ct), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NFR-19, #2141: URI・本文は載せない（個人資料の所在と中身）。どちらの本文かと例外の型だけを残す。
            log.LogWarning("同期競合の {Side} 版の本文をストレージから取り出せなかった（{ErrorType}）。応答は「取得できなかった」として返す。",
                side, ex.GetType().Name);
            return (string.Empty, true);
        }
    }
}
