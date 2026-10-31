using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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

        var (settings, changed) = await ApplyStateAsync(db, owner, pause, DateTimeOffset.UtcNow, ct);

        // 状態が変わったときだけ監査へ残す（冪等の再送・同時の 2 本目で行を増やさない）。
        if (changed)
            audit.Record(pause ? "private-note.sync.pause" : "private-note.sync.resume", owner, "granted",
                pause ? $"period={settings.PausePeriodId}" : null);

        var notes = await SyncSettingsEndpoints.ActiveNotesAsync(db, owner, ct);
        return Results.Ok(SyncSettingsEndpoints.ToDto(settings, notes));
    }

    // 同時の要求の衝突を読み直す回数。
    internal const int MaxAttempts = 3;

    // ADR-0145 決定 1・3: 状態を切り替えて保存し、（保存後の設定, 状態が変わったか）を返す。
    // 🔴 **同時の 2 本を 1 つの停止期間に収める**（AI レビュー 🟡2・🟡3）。
    //   - 初回の行の作成が同時に 2 本 → 主キーの衝突（`DbUpdateException`）。
    //   - 未停止の行への一時停止が同時に 2 本 → `PausePeriodId`（同時実行トークン）の衝突（`DbUpdateConcurrencyException`）。
    //   どちらも追跡を捨てて読み直し、もう一度当てる。読み直した行が既に停止中なら `Pause` は何もしない ＝ 冪等の 200。
    //   後勝ちで `PausePeriodId` を書き換えると、同じ停止の拒否が同期履歴の 2 行に割れる。
    internal static async Task<(Domain.SyncSettings Settings, bool Changed)> ApplyStateAsync(DocumentDbContext db,
        string owner, bool pause, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var settings = await db.SyncSettings.FindAsync([owner], ct);
            if (settings is null)
            {
                settings = Domain.SyncSettings.Create(owner, now);
                db.SyncSettings.Add(settings);
            }
            var wasPaused = settings.IsPaused;
            if (pause) settings.Pause(now);
            else settings.Resume(now);
            try
            {
                await db.SaveChangesAsync(ct);
                return (settings, wasPaused != settings.IsPaused);
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
