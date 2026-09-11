using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;

using DocumentService.Features.PrivateNotes;

namespace DocumentService.Features.PrivateNotes.Maintenance;

// FR-19, FR-20, FR-22, ADR-0037 決定 5・6・16・18, ADR-0096 決定 1・2, IADR-0215 決定 5,
// [[IADR-0270]] 決定 6, [[IADR-0431]]:
// 個人資料の定期処理。①90 日経過の自動物理削除（＋事後通知 ①-c）②版履歴の刈り取り
// （直近 50 版かつ 90 日）③完全削除 7 日前通知（①-b）④週次の削除通知（①-a）
// ⑤同期トークンの期限 7 日前通知（③）⑥**退職 30 日後の完全削除**（通知なし。#1409）。
//
// 🔴 **⑥と①は別の時計である。** ①は ADR-0037 決定 5 の「論理削除から 90 日」、
// ⑥は ADR-0036 D-09 の「退職から 30 日」であり、起点も対象も違う（⑥は論理削除の有無を見ない）。
//
// **検知はデータの在る側（本サービス）で行う**（IADR-0215 決定 5 の表は NotificationService の
// スケジューラとしたが、判定に要るデータは本サービスの DB にあり DB per Service の下で越境
// 読みできない。原則「時間が契機ならバッチ」は維持し、バッチの居場所だけをデータ側へ移す。
// [[IADR-0270]] 決定 6）。
//
// `now` を引数に取るのはテストのためである（30 日・90 日・7 日・週次の時刻依存を実時間に
// 依存せず検証する）。
//
// ★［2026-08-28 追加 / #600］**発火記録は送出より先に確定させる**（IADR-0215 決定 5-a）。
// 従前はいずれの通知も「送出 → 記録 → SaveChanges」の順で、**送出後・保存前にプロセスが落ちると
// 次周期で同じ通知をもう一度送った**。受け口の重複抑止はペイロード 6 項目の完全一致でしか畳まず、
// `occurredAt` が変わる再検知は畳まれない（時間窓で丸めると発火記録と二重に効いて通知が消えるため、
// 受け側では意図的に丸めていない）。**重複を止められるのは発火側だけである。**
// 受容するトレードオフ: **記録が残ったのに送出に失敗した通知は再送されない**（FR-22 の受け入れ基準は
// 「各 1 回」であり、多いほうが要求違反である。少ないほうは計器 notification.dispatch.total で
// `unreachable` として観測できる）。
// ①-c（事後通知）だけは発火記録を持たない —— **行そのものが消えるため構造的に 1 回**である。
public sealed class PrivateNoteMaintenanceService(
    DocumentDbContext db,
    IPrivateNoteNotifier notifier,
    IDocumentDeletedPublisher deletedBus,
    IAuditLogger audit,
    DocumentService.Features.Documents.DocumentObjectPurger purger,
    IOwnerRetentionDirectory ownerRetention,
    ILogger<PrivateNoteMaintenanceService> logger)
{
    public async Task RunAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        // ★［2026-09-11 追加 / #1409・ADR-0096 決定 1・2・[[IADR-0431]]］⑥退職 30 日後の完全削除。
        // 🔴 **90 日の器（`PurgeExpiredAsync`）より先に走らせる。** 退職者の論理削除済み資料が
        // 90 日側で拾われると、FR-22 ①-c の事後通知が**無効化済みの宛先へ**飛ぶ
        // （ADR-0096 決定 1「届かない通知を送る設計にしない」）。順序がこの性質を担っている。
        await PurgeDepartedOwnersAsync(now, ct);
        await PurgeExpiredAsync(now, ct);
        await PruneVersionsAsync(now, ct);
        await NotifyPurgeImminentAsync(now, ct);
        await NotifyWeeklyDigestAsync(now, ct);
        await NotifyTokenExpiryAsync(now, ct);
    }

    // FR-19, UC-11, SC-19, SC-10, 計画 ADR-0036 D-09, ADR-0057 決定 1・2, ADR-0096 決定 1・2,
    // [[IADR-0296]] 決定 3, [[IADR-0428]] 決定 3, [[IADR-0431]] (#1409):
    // **退職して 30 日の閲覧窓が閉じた利用者の個人資料を完全削除する。**
    //
    // ■ 述語は 1 つだけである（ADR-0096 決定 2「述語を 1 つ足す形であり、新しい定期処理を起こさない」）
    //   `Found && !Enabled && Elapsed`（`OwnerRetentionStatus.IsPurgeable`）。
    //   🔴 **`WithinWindow` / `NotEvaluable` / 有効な所有者 / 引けなかった（`null`）は 1 件も消さない。**
    //   ADR-0057 決定 2 により残余を置かないため、**誤削除は取り返せない**（ADR-0096 §結果）。
    //   人事連携が未配備の間は起点が未供給の利用者が居り、その資料は残る —— **fail-safe の裏面である。**
    //
    // ■ 🔴 論理削除の有無を見ない
    //   窓が閉じた時点で ADR-0096 決定 1 の対象であり、生きている資料も消える。
    //   ここが 90 日の器（`PurgeAt <= now`）と違う唯一の点である。
    //
    // ■ 🔴 通知を送らない（ADR-0096 決定 1）
    //   FR-22 ① の宛先は「所有者本人のみ」と定まっており、本経路の所有者は**無効化済み**である。
    //   `PrivateNoteUsage.RecordUsageAndWarnAsync` も呼ばない（容量警告が同じ宛先へ飛ぶ）。
    //   削除の事実は監査ログと SC-10 の側で見る。
    //
    // ■ 🔴 監査に載せるのは「いつ・誰の・何件」だけである（ADR-0096 決定 1）
    //   **タイトル・本文・資料 ID を載せない** —— 残余を置かないという決定を、ログ経由で破らない。
    //   時刻は監査ログの行そのものが持つ。
    //
    // ■ 失敗は行を残す（ADR-0096 決定 2 / [[IADR-0296]] 決定 3）
    //   `PurgeIsolatedAsync` が文書ごとに隔離し、消せたものだけを今周期の対象にする。
    //   消せなかった資料は行が残り、**所有者の状態が変わらない限り次周期で再入する。**
    //
    // ■ 受容するトレードオフ: **所有者 1 人につき 1 往復**である（日次粒度）。
    //   個人資料を持つ所有者の数だけ認可サービスを呼ぶ。名簿の一括照会の口は s2s の面に無く
    //   （[[IADR-0401]] 決定 2 が列挙を出さないと決めた）、**面を広げるより往復を受ける**。
    private async Task PurgeDepartedOwnersAsync(DateTimeOffset now, CancellationToken ct)
    {
        var owners = await db.PrivateNotes.Select(n => n.OwnerId).Distinct().ToListAsync(ct);
        if (owners.Count == 0) return;

        foreach (var owner in owners)
        {
            var status = await ownerRetention.GetAsync(owner, ct);
            // 🔴 `null`（引けなかった）はここで落ちる。**「窓が閉じた」へ倒さない。**
            if (status is null || !status.IsPurgeable) continue;

            await PurgeAllOwnedAsync(owner, now, ct);
        }
    }

    // ADR-0096 決定 1: 1 人分の完全削除。射程は ADR-0057 決定 1 と同じ
    // （DB 記録・本文の実体・索引）。索引はこの 3 つ目であり `DocumentDeleted` が運ぶ。
    private async Task PurgeAllOwnedAsync(string owner, DateTimeOffset now, CancellationToken ct)
    {
        var owned = await db.PrivateNotes.Where(n => n.OwnerId == owner).ToListAsync(ct);
        if (owned.Count == 0) return;

        // 実体を先に消し、**消せたものだけ**を今周期の削除対象にする（行だけ消してオブジェクトを
        // 残すことは絶対にしない —— 参照が失われ回復不能になる）。
        var ids = await purger.PurgeIsolatedAsync(owned.Select(n => n.DocumentId).ToList(), ct);
        if (ids.Count == 0) return;

        var due = owned.Where(n => ids.Contains(n.DocumentId)).ToList();
        var docs = await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync(ct);
        db.Documents.RemoveRange(docs);
        db.PrivateNotes.RemoveRange(due);
        await db.SaveChangesAsync(ct);

        // 🔴 件数と所有者だけ。**タイトルは載せない。**
        audit.Record("private-note.purge.departed", owner, "granted", $"count={due.Count}");

        // ADR-0027 / E3a: DocumentDeleted の発行は Wolverine（IDocumentDeletedPublisher 経由）。
        foreach (var id in ids)
            await deletedBus.PublishDeletedAsync(id, now, ct);

        // 🔴 所有者 ID を構造化プロパティへ出さない（監査ログ側に既にある。二重に散らさない）。
        logger.LogInformation(
            "閲覧窓の閉じた個人資料を完全削除した（{Count} 件）", due.Count);
    }

    // ADR-0037 決定 5: 論理削除から 90 日（PurgeAt）を経過した資料を自動的に物理削除する（復元不可）。
    // 決定 6-③: 実行後に事後通知（件数のみ）。決定 9・11-①: 監査は「誰が・いつ・何件」。
    //
    // ★ ADR-0057 決定 1 / [[IADR-0296]]: **オブジェクトストレージの本文も消す。**
    // 🔴 **対話操作と違い、ここは文書ごとに隔離する**（同 IADR 決定 3）——
    // 1 件の実体削除の失敗で周期全体を止めると、**無関係な資料の期限超過が積み上がる**。
    // 消せなかった文書は**行を残す**ので `PurgeAt <= now` を満たしたままであり、次周期で再入する。
    // 「行だけ消してオブジェクトを残す」ことだけは絶対にしない（参照が失われ回復不能になる）。
    private async Task PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await db.PrivateNotes
            .Where(n => n.DeletedAt != null && n.PurgeAt != null && n.PurgeAt <= now)
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        // オブジェクトを先に消し、**消せたものだけ**を今周期の削除対象にする。
        var ids = await purger.PurgeIsolatedAsync(
            candidates.Select(n => n.DocumentId).ToList(), ct);
        if (ids.Count == 0) return;

        var due = candidates.Where(n => ids.Contains(n.DocumentId)).ToList();
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

        // ADR-0027 / E3a: DocumentDeleted の発行は Wolverine（IDocumentDeletedPublisher 経由）。
        foreach (var id in ids)
            await deletedBus.PublishDeletedAsync(id, now, ct);
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
    // ★ 発火記録を送出より先に確定させる（IADR-0215 決定 5-a。§RunAsync の注記）。
    private async Task NotifyPurgeImminentAsync(DateTimeOffset now, CancellationToken ct)
    {
        var horizon = now.AddDays(7);
        var imminent = await db.PrivateNotes
            .Where(n => n.DeletedAt != null && n.PurgeAt != null
                && n.PurgeAt > now && n.PurgeAt <= horizon
                && n.PurgeImminentNotifiedAt == null)
            .ToListAsync(ct);
        if (imminent.Count == 0) return;

        var batches = imminent.GroupBy(n => n.OwnerId)
            .Select(g => (Owner: g.Key, Count: g.Count(), Deadline: g.Min(n => n.PurgeAt)))
            .ToList();
        foreach (var note in imminent) note.MarkPurgeImminentNotified(now);
        await db.SaveChangesAsync(ct);

        foreach (var (owner, count, deadline) in batches)
        {
            await notifier.NotifyAsync(owner,
                PrivateNoteNotificationKinds.PrivateNotePurgeImminent, now,
                count: count, deadline: deadline, ct: ct);
        }
    }

    // FR-22 ①-a, ADR-0037 決定 6-①: 週次通知。論理削除済みの件数と最短の完全削除期限のみを運ぶ。
    // 週次の判定は所有者ごとの前回送出時刻（7 日以上経過で送る）で行う —— 曜日固定にしないのは、
    // プロセスの再起動・停止で曜日を取りこぼしても翌実行で追いつくようにするためである。
    // ★ 発火記録を送出より先に確定させる（IADR-0215 決定 5-a。§RunAsync の注記）。
    private async Task NotifyWeeklyDigestAsync(DateTimeOffset now, CancellationToken ct)
    {
        var deleted = await db.PrivateNotes.Where(n => n.DeletedAt != null).ToListAsync(ct);
        if (deleted.Count == 0) return;

        var batches = new List<(string Owner, int Count, DateTimeOffset? Deadline)>();
        foreach (var byOwner in deleted.GroupBy(n => n.OwnerId))
        {
            var quota = await PrivateNoteUsage.GetOrCreateQuotaAsync(db, byOwner.Key, now, ct);
            if (quota.WeeklyDigestSentAt is { } last && now - last < TimeSpan.FromDays(7))
                continue;

            quota.MarkWeeklyDigestSent(now);
            batches.Add((byOwner.Key, byOwner.Count(), byOwner.Min(n => n.PurgeAt)));
        }
        await db.SaveChangesAsync(ct);

        foreach (var (owner, count, deadline) in batches)
        {
            await notifier.NotifyAsync(owner,
                PrivateNoteNotificationKinds.PrivateNotePurgeWeekly, now,
                count: count, deadline: deadline, ct: ct);
        }
    }

    // FR-22 ③, ADR-0037 決定 18: 同期トークンの期限 7 日前通知（件数と期限のみ）。
    // 発火記録（ExpiryNotifiedAt）で 1 回だけ送る。**期限切れ当日の追加通知は設けない**
    // （7 日の窓を過ぎたトークンはもう通知しない —— 当日通知を作らない決定の実装形である）。
    // ★ 発火記録を送出より先に確定させる（IADR-0215 決定 5-a。§RunAsync の注記）。
    private async Task NotifyTokenExpiryAsync(DateTimeOffset now, CancellationToken ct)
    {
        var horizon = now.AddDays(SyncDevice.ExpiryNoticeDays);
        var expiring = await db.SyncDevices
            .Where(d => d.RevokedAt == null && d.ExpiresAt > now && d.ExpiresAt <= horizon
                && d.ExpiryNotifiedAt == null)
            .ToListAsync(ct);
        if (expiring.Count == 0) return;

        var batches = expiring.GroupBy(d => d.OwnerId)
            .Select(g => (Owner: g.Key, Count: g.Count(), Deadline: g.Min(d => d.ExpiresAt)))
            .ToList();
        foreach (var device in expiring) device.MarkExpiryNotified(now);
        await db.SaveChangesAsync(ct);

        foreach (var (owner, count, deadline) in batches)
        {
            await notifier.NotifyAsync(owner,
                PrivateNoteNotificationKinds.SyncTokenExpiry, now,
                count: count, deadline: deadline, ct: ct);
        }
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
