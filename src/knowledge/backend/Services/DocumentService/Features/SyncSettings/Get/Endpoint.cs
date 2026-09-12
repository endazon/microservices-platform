using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;

namespace DocumentService.Features.SyncSettings.Get;

// FR-20, SC-20 主要素 3, ADR-0037 決定 3: 同期対象範囲の取得。
// **未設定は 404 ではなく `targetFolders: []`**（＝全資料が対象）である ——
// 「まだ設定していない」と「設定できない」を画面が取り違えないようにする。
internal static class GetSyncSettingsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (HttpContext http, DocumentDbContext db, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();

            var settings = await db.SyncSettings.FindAsync([owner], ct);
            var notes = await SyncSettingsEndpoints.ActiveNotesAsync(db, owner, ct);
            return Results.Ok(SyncSettingsEndpoints.ToDto(settings, notes));
        });
    }
}
