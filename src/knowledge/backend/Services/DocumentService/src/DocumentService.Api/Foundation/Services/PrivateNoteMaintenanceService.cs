using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Persistence;
using DocumentService.Api.Foundation.Ports;
using Knowledge.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Api.Foundation.Services;

// FR-19, FR-20, FR-22, ADR-0037 決定 5・6・16・18, IADR-0215 決定 5, [[IADR-0270]] 決定 6:
// 個人資料の定期処理。①90 日経過の自動物理削除（＋事後通知 ①-c）②版履歴の刈り取り
// （直近 50 版かつ 90 日）③完全削除 7 日前通知（①-b）④週次の削除通知（①-a）
// ⑤同期トークンの期限 7 日前通知（③）。
//
// **検知はデータの在る側（本サービス）で行う**（IADR-0215 決定 5 の表は NotificationService の
// スケジューラとしたが、判定に要るデータは本サービスの DB にあり DB per Service の下で越境
// 読みできない。原則「時間が契機ならバッチ」は維持し、バッチの居場所だけをデータ側へ移す。
// [[IADR-0270]] 決定 6）。
//
// `now` を引数に取るのはテストのためである（30 日・90 日・7 日・週次の時刻依存を実時間に
// 依存せず検証する）。
public sealed class PrivateNoteMaintenanceService(
    DocumentDbContext db,
    IPrivateNoteNotifier notifier,
    IPublishEndpoint bus,
    IAuditLogger audit,
    ILogger<PrivateNoteMaintenanceService> logger)
{
    public async Task RunAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await PurgeExpiredAsync(now, ct);
        await PruneVersionsAsync(now, ct);
        await NotifyPurgeImminentAsync(now, ct);
        await NotifyWeeklyDigestAsync(now, ct);
        await NotifyTokenExpiryAsync(now, ct);
    }

    // ADR-0037 決定 5: 論理削除から 90 日（PurgeAt）を経過した資料を自動的に物理削除する（復元不可）。
    // 決定 6-③: 実行後に事後通知（件数のみ）。決定 9・11-①: 監査は「誰が・いつ・何件」。
    private async Task PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.PrivateNotes
            .Where(n => n.DeletedAt != null && n.PurgeAt != null && n.PurgeAt <= now)
            .ToListAsync(ct);
        if (due.Count == 0) return;

        var ids = due.Select(n => n.DocumentId).ToList();
        var docs = await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync(ct);
        db.Documents.RemoveRange(docs);
        db.PrivateNotes.RemoveRange(due);
        await db.SaveChangesAsync(ct);

        foreach (var byOwner in due.GroupBy(n => n.OwnerId))
        {
            audit.Record("private-note.purge.auto", byOwner.Key, "granted",
                $"count={byOwner.Count()}");
            // FR-22 ①-c: 完全削除の事後通知（件数のみ。タイトルを含めない）。
            await notifier.NotifyAsync(byOwner.Key,
                PrivateNoteNotificationKinds.PrivateNotePurgeDone, now,
                count: byOwner.Count(), ct: ct);
            // 使用量が下がって閾値を割れば、警告の発火記録を再武装する（FR-22 ②）。
            await PrivateNoteUsage.RecordUsageAndWarnAsync(db, notifier, byOwner.Key, now, ct);
        }
        await db.SaveChangesAsync(ct);

        foreach (var id in ids)
            await bus.Publish(new DocumentDeleted(id, now), ct);
        logger.LogInformation("個人資料の自動物理削除を実行した（{Count} 件）", due.Count);
    }

    // ADR-0037 決定 16: 版履歴の保持上限。**直近 50 版から外れ、かつ作成から 90 日を超えた版**を
    // 古い順に物理削除する（直近 50 版以内なら 90 日超でも残し、90 日以内なら 50 版超でも残す。
    // 両方の条件を満たさなくなった版だけが落ちる）。対象は個人資料（台帳を持つ文書）のみである
    // —— 決定 16 は FR-19 の版履歴に対する裁定であり、組織文書の版履歴には適用しない。
    private async Task PruneVersionsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-PrivateNote.RetentionDays);
        var noteIds = await db.PrivateNotes.Select(n => n.DocumentId).ToListAsync(ct);
        if (noteIds.Count == 0) return;

        var pruned = 0;
        foreach (var noteId in noteIds)
        {
            var versions = await db.DocumentVersions
                .Where(v => v.DocumentId == noteId)
                .OrderByDescending(v => v.Version)
                .ToListAsync(ct);
            var beyondKeepCount = versions.Skip(PrivateNote.VersionKeepCount);
            var toDelete = beyondKeepCount.Where(v => v.CreatedAt < cutoff).ToList();
            if (toDelete.Count == 0) continue;

            db.DocumentVersions.RemoveRange(toDelete);
            pruned += toDelete.Count;
        }
        if (pruned > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("個人資料の版履歴を刈り取った（{Count} 版）", pruned);
        }
    }

    // FR-22 ①-b, ADR-0037 決定 6-②: 完全削除の 7 日前に別建ての通知を出す（件数と期限のみ）。
    // 発火記録（PurgeImminentNotifiedAt）で 1 回だけ送る。
    private async Task NotifyPurgeImminentAsync(DateTimeOffset now, CancellationToken ct)
    {
        var horizon = now.AddDays(7);
        var imminent = await db.PrivateNotes
            .Where(n => n.DeletedAt != null && n.PurgeAt != null
                && n.PurgeAt > now && n.PurgeAt <= horizon
                && n.PurgeImminentNotifiedAt == null)
            .ToListAsync(ct);
        if (imminent.Count == 0) return;

        foreach (var byOwner in imminent.GroupBy(n => n.OwnerId))
        {
            await notifier.NotifyAsync(byOwner.Key,
                PrivateNoteNotificationKinds.PrivateNotePurgeImminent, now,
                count: byOwner.Count(), deadline: byOwner.Min(n => n.PurgeAt), ct: ct);
            foreach (var note in byOwner) note.MarkPurgeImminentNotified(now);
        }
        await db.SaveChangesAsync(ct);
    }

    // FR-22 ①-a, ADR-0037 決定 6-①: 週次通知。論理削除済みの件数と最短の完全削除期限のみを運ぶ。
    // 週次の判定は所有者ごとの前回送出時刻（7 日以上経過で送る）で行う —— 曜日固定にしないのは、
    // プロセスの再起動・停止で曜日を取りこぼしても翌実行で追いつくようにするためである。
    private async Task NotifyWeeklyDigestAsync(DateTimeOffset now, CancellationToken ct)
    {
        var deleted = await db.PrivateNotes.Where(n => n.DeletedAt != null).ToListAsync(ct);
        if (deleted.Count == 0) return;

        foreach (var byOwner in deleted.GroupBy(n => n.OwnerId))
        {
            var quota = await PrivateNoteUsage.GetOrCreateQuotaAsync(db, byOwner.Key, now, ct);
            if (quota.WeeklyDigestSentAt is { } last && now - last < TimeSpan.FromDays(7))
                continue;

            await notifier.NotifyAsync(byOwner.Key,
                PrivateNoteNotificationKinds.PrivateNotePurgeWeekly, now,
                count: byOwner.Count(), deadline: byOwner.Min(n => n.PurgeAt), ct: ct);
            quota.MarkWeeklyDigestSent(now);
        }
        await db.SaveChangesAsync(ct);
    }

    // FR-22 ③, ADR-0037 決定 18: 同期トークンの期限 7 日前通知（件数と期限のみ）。
    // 発火記録（ExpiryNotifiedAt）で 1 回だけ送る。**期限切れ当日の追加通知は設けない**
    // （7 日の窓を過ぎたトークンはもう通知しない —— 当日通知を作らない決定の実装形である）。
    private async Task NotifyTokenExpiryAsync(DateTimeOffset now, CancellationToken ct)
    {
        var horizon = now.AddDays(SyncDevice.ExpiryNoticeDays);
        var expiring = await db.SyncDevices
            .Where(d => d.RevokedAt == null && d.ExpiresAt > now && d.ExpiresAt <= horizon
                && d.ExpiryNotifiedAt == null)
            .ToListAsync(ct);
        if (expiring.Count == 0) return;

        foreach (var byOwner in expiring.GroupBy(d => d.OwnerId))
        {
            await notifier.NotifyAsync(byOwner.Key,
                PrivateNoteNotificationKinds.SyncTokenExpiry, now,
                count: byOwner.Count(), deadline: byOwner.Min(d => d.ExpiresAt), ct: ct);
            foreach (var device in byOwner) device.MarkExpiryNotified(now);
        }
        await db.SaveChangesAsync(ct);
    }
}

// FR-19, FR-20, FR-22: 定期処理の起動。日次粒度（IADR-0215 決定 2「3 契機はいずれも日・週の粒度」）。
// **初回実行は起動から 1 周期後**とする —— 起動直後に走らせると、テストホストの立ち上げと
// シードデータの投入が競合する（本番でも再起動のたびに走る必要は無い。最悪 24 時間の遅延は
// 日・週粒度の通知では許容範囲である）。
public sealed class PrivateNoteMaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<PrivateNoteMaintenanceHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var maintenance = scope.ServiceProvider
                        .GetRequiredService<PrivateNoteMaintenanceService>();
                    await maintenance.RunAsync(DateTimeOffset.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 定期処理の失敗でホストを落とさない。次周期で再試行する。
                    logger.LogError(ex, "個人資料の定期処理に失敗した。次周期で再試行する。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // シャットダウン。
        }
    }
}
