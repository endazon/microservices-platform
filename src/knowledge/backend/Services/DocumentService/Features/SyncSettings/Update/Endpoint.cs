using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using FluentValidation;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.SyncSettings.Update;

// FR-20, SC-20 主要素 3, ADR-0037 決定 4, #1442: 同期対象フォルダの**置き換え**（全量）。
//
// 🔴 **対象から外しても資料は削除しない。** ここが触るのは `SyncSettings` の 1 行だけであり、
// `PrivateNote` には一切手を触れない —— 「外す」＝同期停止（`syncState` が `excluded` になる）
// であることを、コードの形で守る（決定 4）。
internal static class UpdateSyncSettingsEndpoint
{
    // 同時の要求の衝突を読み直す回数。
    internal const int MaxAttempts = 3;

    private static Domain.SyncSettings AddNew(DocumentDbContext db, string owner, DateTimeOffset now)
    {
        var created = Domain.SyncSettings.Create(owner, now);
        db.SyncSettings.Add(created);
        return created;
    }

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

            // FR-20, [[IADR-0667]] (#2378): 同期設定の行は停止期間を同時実行トークンに持つ。同時の一時停止・再開・初回の作成と
            // 衝突したら、追跡を捨てて読み直し、同じ置き換えをもう一度当てる（停止の状態は読み直した値のまま残す）。
            Domain.SyncSettings settings;
            for (var attempt = 1; ; attempt++)
            {
                settings = await db.SyncSettings.FindAsync([owner], ct) ?? AddNew(db, owner, now);
                settings.Replace(folders, now);
                // FR-19, ADR-0143 決定 1・2 (#2120): 省略（null）なら変えない。変えても既存の資料には触れない。
                if (req.AiInputDefault is { } aiDefault) settings.SetAiInputDefault(aiDefault, now);
                try
                {
                    await db.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateException) when (attempt < MaxAttempts)
                {
                    db.ChangeTracker.Clear();
                }
            }

            var notes = await SyncSettingsEndpoints.ActiveNotesAsync(db, owner, ct);
            return Results.Ok(SyncSettingsEndpoints.ToDto(settings, notes));
        });
    }
}
