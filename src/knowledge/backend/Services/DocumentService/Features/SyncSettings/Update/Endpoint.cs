using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.SyncSettings.Update;

// FR-20, SC-20 主要素 3, ADR-0037 決定 4, #1442: 同期対象フォルダの**置き換え**（全量）。
//
// 🔴 **対象から外しても資料は削除しない。** ここが触るのは `SyncSettings` の 1 行だけであり、
// `PrivateNote` には一切手を触れない —— 「外す」＝同期停止（`syncState` が `excluded` になる）
// であることを、コードの形で守る（決定 4）。
internal static class UpdateSyncSettingsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPut("/", async (UpdateSyncSettingsRequest req,
            IValidator<UpdateSyncSettingsRequest> validator, HttpContext http,
            DocumentDbContext db, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();

            // 🔴 **この呼び出しは 401 の後ろ・DB 照会の前**でなければならない（他の端点と同じ順）。
            var gate = validator.Validate(req);
            if (!gate.IsValid) return ValidationProblems.FirstViolation(gate);

            var now = DateTimeOffset.UtcNow;
            // 正規化は**保存する側**が行う（検証器は値を書き換えられない。同じ関数を両方が呼ぶ）。
            var folders = req.TargetFolders.Select(Domain.SyncSettings.NormalizeFolder).ToList();

            var settings = await db.SyncSettings.FindAsync([owner], ct);
            if (settings is null)
            {
                settings = Domain.SyncSettings.Create(owner, now);
                db.SyncSettings.Add(settings);
            }
            settings.Replace(folders, now);
            await db.SaveChangesAsync(ct);

            var notes = await SyncSettingsEndpoints.ActiveNotesAsync(db, owner, ct);
            return Results.Ok(SyncSettingsEndpoints.ToDto(settings, notes));
        });
    }
}
