using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NotificationService.Api.Foundation.Domain;

namespace NotificationService.Api.Tests;

// FR-22, IADR-0215 決定 2: アプリ内通知の保持期間（既定 90 日）。
// **90 日は計画に根拠が無い実装側の判断である**（ADR-0037 決定 5 の保管期間へ揃えた）。
public class NotificationRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    // FR-22: **保持期間を過ぎた通知は物理削除され、期間内のものは残る。**
    [Fact]
    public async Task 保持期間を過ぎた通知だけが削除される()
    {
        using var h = new NotificationHarness(retentionDays: 90);

        var old = Notification.Create("alice", NotificationKinds.PrivateNotePurgeDone, Now.AddDays(-91), count: 1);
        var recent = Notification.Create("alice", NotificationKinds.PrivateNotePurgeDone, Now.AddDays(-89), count: 1);
        h.Db.Notifications.AddRange(old, recent);
        await h.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var purged = await h.Retention().PurgeExpiredAsync(Now, TestContext.Current.CancellationToken);

        purged.Should().Be(1);
        var remaining = await h.Db.Notifications.ToListAsync(TestContext.Current.CancellationToken);
        remaining.Should().ContainSingle(n => n.Id == recent.Id);
    }

    // FR-22, SC-10: **outbox の記録は通知本体より長く残す**（送出の観測面は消さない）。
    [Fact]
    public async Task 保持期間の掃除は送出記録を消さない()
    {
        using var h = new NotificationHarness(retentionDays: 90);

        var notification = await h.Publisher().PublishAsync(
            "alice", NotificationKinds.SyncTokenExpiry, Now.AddDays(-100),
            count: 1, ct: TestContext.Current.CancellationToken);

        await h.Retention().PurgeExpiredAsync(Now, TestContext.Current.CancellationToken);

        (await h.Db.Notifications.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await h.Db.EmailOutbox.CountAsync(o => o.NotificationId == notification.Id,
            TestContext.Current.CancellationToken)).Should().Be(1);
    }
}
