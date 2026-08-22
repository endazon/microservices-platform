using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NotificationService.Api.Foundation.Domain;
using NotificationService.Api.Foundation.Services;

namespace NotificationService.Api.Tests;

// FR-22, SC-10, ADR-0045 決定 3・8: 受け入れ基準「送信上限を超える通知が静かに落ちない」（AC-5）。
//
// ★ **「落ちない」ではなく「静かに落ちない」である。** 上限に触れたぶんが送られないこと自体は
// 設計どおりであり、確かめるべきは**その事実が状態・監査ログ・メトリクスに残ること**である。
public class EmailSendRateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    // FR-22, SC-10: **上限を超えたぶんは繰り越し（deferred）として残る**（AC-5）。
    [Fact]
    public async Task 日次上限を超えた分は繰り越しとして記録される()
    {
        using var h = new NotificationHarness(dailyEmailLimit: 1);
        var publisher = h.Publisher();

        await publisher.PublishAsync("alice", NotificationKinds.PrivateNotePurgeWeekly, Now.AddMinutes(-2),
            count: 1, deadline: Now.AddDays(30), ct: TestContext.Current.CancellationToken);
        await publisher.PublishAsync("bob", NotificationKinds.PrivateNotePurgeWeekly, Now.AddMinutes(-1),
            count: 2, deadline: Now.AddDays(30), ct: TestContext.Current.CancellationToken);

        var summary = await h.Dispatcher(new RecordingEmailTransport())
            .DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        summary.Sent.Should().Be(1);
        summary.Deferred.Should().Be(1, "★ 上限を超えたぶんは捨てずに繰り越す");

        var deferred = await h.Db.EmailOutbox
            .SingleAsync(o => o.Status == EmailOutboxStatus.Deferred, TestContext.Current.CancellationToken);
        deferred.LastReason.Should().Be(EmailOutboxDispatcher.ReasonDailyLimit);
        deferred.DeferralCount.Should().Be(1);
    }

    // FR-22, SC-10, ADR-0045 決定 8: **上限到達そのものが 1 事象として監査ログに残る**（AC-5）。
    [Fact]
    public async Task 上限到達そのものが監査ログに残る()
    {
        using var h = new NotificationHarness(dailyEmailLimit: 1);
        var publisher = h.Publisher();

        await publisher.PublishAsync("alice", NotificationKinds.StorageQuotaWarning, Now.AddMinutes(-2),
            thresholdPercent: 80, ct: TestContext.Current.CancellationToken);
        await publisher.PublishAsync("bob", NotificationKinds.StorageQuotaWarning, Now.AddMinutes(-1),
            thresholdPercent: 95, ct: TestContext.Current.CancellationToken);

        await h.Dispatcher(new RecordingEmailTransport())
            .DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        h.Audit.Records.Should().ContainSingle(r => r.Action == EmailOutboxDispatcher.LimitReachedAction,
            "★ 上限に当たっていることがダッシュボードで先に見える必要がある");
    }

    // FR-22: **繰り越したものは翌日に送られる**（AC-5。繰り越しが行き止まりにならないこと）。
    [Fact]
    public async Task 繰り越したものは翌日に送出される()
    {
        using var h = new NotificationHarness(dailyEmailLimit: 1);
        var publisher = h.Publisher();
        var transport = new RecordingEmailTransport();

        await publisher.PublishAsync("alice", NotificationKinds.PrivateNotePurgeWeekly, Now.AddMinutes(-2),
            count: 1, deadline: Now.AddDays(30), ct: TestContext.Current.CancellationToken);
        await publisher.PublishAsync("bob", NotificationKinds.PrivateNotePurgeWeekly, Now.AddMinutes(-1),
            count: 2, deadline: Now.AddDays(30), ct: TestContext.Current.CancellationToken);

        var dispatcher = h.Dispatcher(transport);
        await dispatcher.DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        var nextDay = await dispatcher.DispatchPendingAsync(Now.AddDays(1), TestContext.Current.CancellationToken);

        nextDay.Sent.Should().Be(1, "★ 当日の送信数で数えるので、翌日は上限が空く");
        transport.Sent.Should().HaveCount(2);
        (await h.Db.EmailOutbox.CountAsync(o => o.Status == EmailOutboxStatus.Sent, TestContext.Current.CancellationToken))
            .Should().Be(2);
    }

    // FR-22, IADR-0215 決定 4: **期限を過ぎた繰り越しは破棄するが、黙っては消さない**（AC-5）。
    // 完全削除の 7 日前通知が完全削除の後に届いても意味が無い。だから捨てる。だから記録する。
    [Fact]
    public async Task 期限を過ぎた繰り越しは理由つきで破棄される()
    {
        using var h = new NotificationHarness(dailyEmailLimit: 500);

        await h.Publisher().PublishAsync("alice", NotificationKinds.PrivateNotePurgeImminent, Now.AddDays(-8),
            count: 1, deadline: Now.AddDays(-1), ct: TestContext.Current.CancellationToken);

        var summary = await h.Dispatcher(new RecordingEmailTransport())
            .DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        summary.Dropped.Should().Be(1);

        var entry = await h.Db.EmailOutbox.SingleAsync(TestContext.Current.CancellationToken);
        entry.Status.Should().Be(EmailOutboxStatus.Dropped);
        entry.LastReason.Should().Be(EmailOutboxDispatcher.ReasonDeadlinePassed);

        h.Audit.Records.Should().ContainSingle(r =>
            r.Action == EmailOutboxDispatcher.AuditActionPrefix + EmailOutboxStatus.Dropped);
    }

    // FR-22, SC-10, ADR-0045 決定 8: **4 つの結末すべてが監査ログに載る**（AC-5 の中核）。
    // 1 つでも載らない結末があれば、そこが「静かに落ちる」経路になる。
    [Theory]
    [InlineData(EmailOutboxStatus.Sent)]
    [InlineData(EmailOutboxStatus.Deferred)]
    [InlineData(EmailOutboxStatus.Dropped)]
    [InlineData(EmailOutboxStatus.Failed)]
    public async Task すべての結末が監査ログに載る(string outcome)
    {
        var limit = outcome == EmailOutboxStatus.Deferred ? 0 : 500;
        using var h = new NotificationHarness(dailyEmailLimit: limit);

        var deadline = outcome == EmailOutboxStatus.Dropped ? Now.AddDays(-1) : Now.AddDays(30);
        await h.Publisher().PublishAsync("alice", NotificationKinds.SyncTokenExpiry, Now.AddMinutes(-1),
            count: 1, deadline: deadline, ct: TestContext.Current.CancellationToken);

        var transport = outcome == EmailOutboxStatus.Failed
            ? (Foundation.Ports.IEmailTransport)new AlwaysFailingEmailTransport()
            : new RecordingEmailTransport();

        await h.Dispatcher(transport).DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        h.Audit.Records.Should().ContainSingle(r =>
            r.Action == EmailOutboxDispatcher.AuditActionPrefix + outcome && r.Outcome == outcome);

        (await h.Db.EmailOutbox.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(outcome);
    }

    // FR-22: **監査ログの detail に資料由来の文字列を入れない**（AC-2 の監査面）。
    // 監査ログは ABAC の外側（可観測性基盤）へ出るため、通知本文と同じ規律が要る。
    [Fact]
    public async Task 監査ログの詳細は種別と理由語だけで構成される()
    {
        using var h = new NotificationHarness(dailyEmailLimit: 0);

        await h.Publisher().PublishAsync("alice", NotificationKinds.PrivateNotePurgeWeekly, Now,
            count: 3, deadline: Now.AddDays(30), ct: TestContext.Current.CancellationToken);

        await h.Dispatcher(new RecordingEmailTransport())
            .DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        var record = h.Audit.Records.Single(r =>
            r.Action == EmailOutboxDispatcher.AuditActionPrefix + EmailOutboxStatus.Deferred);

        record.Detail.Should().Be(
            $"kind={NotificationKinds.PrivateNotePurgeWeekly} reason={EmailOutboxDispatcher.ReasonDailyLimit}");
    }
}
