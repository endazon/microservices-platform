using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.ObsidianSync;

// FR-20, UC-11, SC-20 主要素 7, 計画 ADR-0145 決定 1〜3, [[IADR-0667]] (#2378): **同期の一時停止の門**。
//
// 🔴 **route group（`/private-notes/sync`）の端点フィルタとして置く。** 5 つの口（manifest・push・pull・delete・move）と、
//   これから足す口を、端点が呼び忘れても覆う（決定 2「口ごとに分けると、停止中に削除だけが届く」）。
// 🔴 **端末が解けない要求（401 の系列）には何もしない。** トークンの欠落・不正・期限切れ・失効とアカウントの無効化は、
//   停止中でも従前どおり端点が 401 を返す（ADR-0114。停止中の拒否と取り違えさせない）。
// 🔴 **拒む応答は 423 で、本文は `{"error":"sync_paused","pausedAt":…}`**（決定 2 の最低条件: 401 を使わない・
//   機械可読な理由コードを含む）。409 は競合、401 はトークンに使われている。`error` の鍵は既存の 409 の本文と同じ。
// 同期履歴には停止期間ごとに 1 行だけを残し（決定 3）、構造化ログには拒否ごとに 1 行を出す。
internal static class SyncPauseGate
{
    internal const string AuditAction = "private-note.sync.paused";

    // 同時の初回の拒否（一意索引）・同時の更新（同時実行トークン）の衝突を再試行する回数。
    internal const int MaxRecordAttempts = 3;

    internal static async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx,
        EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var ct = http.RequestAborted;
        var db = http.RequestServices.GetRequiredService<DocumentDbContext>();
        var now = DateTimeOffset.UtcNow;

        var device = await ObsidianSyncEndpoints.ResolveDeviceAsync(http, db, now, ct);
        if (device is null) return await next(ctx);

        var settings = await db.SyncSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerId == device.OwnerId, ct);
        if (settings is not { PausedAt: { } pausedAt, PausePeriodId: { } period }) return await next(ctx);

        await RecordRejectionAsync(http, db, device.OwnerId, period, now, ct);
        http.RequestServices.GetRequiredService<IAuditLogger>().Record(AuditAction, device.OwnerId, "denied",
            $"device={device.Id} reason={SyncFailureReasons.Paused}");

        return Results.Json(new SyncPausedResponse(SyncFailureReasons.Paused, pausedAt),
            statusCode: StatusCodes.Status423Locked);
    }

    // ADR-0145 決定 3: 停止期間の 1 行を作るか、回数と最後の時刻を進める。🔴 **記録に失敗しても拒否はやめない**
    // （統制は拒否であり、記録は利用者への説明である）。再試行を使い切ったら警告を残して諦める。
    private static async Task RecordRejectionAsync(HttpContext http, DocumentDbContext db, string owner,
        Guid period, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var row = await db.SyncAuditEntries
                    .FirstOrDefaultAsync(a => a.OwnerId == owner && a.PausePeriodId == period, ct);
                if (row is null) db.SyncAuditEntries.Add(SyncAuditEntry.PausedRejection(owner, period, now));
                else row.CountRejection(now);
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex)
            {
                foreach (var entry in db.ChangeTracker.Entries<SyncAuditEntry>().ToList())
                    entry.State = EntityState.Detached;
                if (attempt >= MaxRecordAttempts)
                {
                    http.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(SyncPauseGate))
                        .LogWarning(ex, "一時停止中の拒否を同期履歴へ記録できませんでした（拒否はしています）。");
                    return;
                }
            }
        }
    }
}

// ADR-0145 決定 2: 停止中の拒否の本文。`Error` は理由コード（`SyncFailureReasons.Paused`）、`PausedAt` は停止期間の始まり。
internal sealed record SyncPausedResponse(string Error, DateTimeOffset PausedAt);
