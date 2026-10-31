using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.SyncSettings.Pause;

// FR-20, UC-11, SC-20 主要素 7, 計画 ADR-0145 決定 1, [[IADR-0667]] (#2378): **同期の一時停止と再開**（利用者単位・全端末）。
//
// 🔴 **同期トークン・端末・有効期限には触れない。** ここが触るのは同期設定の 1 行だけである —— 失効（端末の削除・再発行）とは別の
//   操作であり、再開すれば同じトークンで同期が戻る（決定 1）。拒否そのものは同期の口の門（`SyncPauseGate`）が担う（決定 2）。
// どちらも冪等である（停止中の一時停止は停止期間を変えない・有効なときの再開は何もしない）。主体はトークンからしか採らない。
internal static class PauseSyncEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/pause", (HttpContext http, DocumentDbContext db, IAuditLogger audit, CancellationToken ct)
            => ApplyAsync(http, db, audit, pause: true, ct));
        g.MapPost("/resume", (HttpContext http, DocumentDbContext db, IAuditLogger audit, CancellationToken ct)
            => ApplyAsync(http, db, audit, pause: false, ct));
    }

    private static async Task<IResult> ApplyAsync(HttpContext http, DocumentDbContext db, IAuditLogger audit,
        bool pause, CancellationToken ct)
    {
        if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();

        var now = DateTimeOffset.UtcNow;
        var settings = await db.SyncSettings.FindAsync([owner], ct);
        if (settings is null)
        {
            settings = Domain.SyncSettings.Create(owner, now);
            db.SyncSettings.Add(settings);
        }
        var wasPaused = settings.IsPaused;
        if (pause) settings.Pause(now);
        else settings.Resume(now);
        await db.SaveChangesAsync(ct);

        // 状態が変わったときだけ監査へ残す（冪等の再送で行を増やさない）。
        if (wasPaused != settings.IsPaused)
            audit.Record(pause ? "private-note.sync.pause" : "private-note.sync.resume", owner, "granted",
                pause ? $"period={settings.PausePeriodId}" : null);

        var notes = await SyncSettingsEndpoints.ActiveNotesAsync(db, owner, ct);
        return Results.Ok(SyncSettingsEndpoints.ToDto(settings, notes));
    }
}
