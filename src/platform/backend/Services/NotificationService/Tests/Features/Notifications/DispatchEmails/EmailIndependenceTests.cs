using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.DispatchEmails;

namespace NotificationService.Tests.Features.Notifications.DispatchEmails;

// FR-22: 受け入れ基準「アプリ内通知が主・メールが補助である。メールが送れない場合もアプリ内通知は届く」（AC-4）。
//
// ★ **この基準は構造で成り立たせてある**（永続化と outbox 投入・送信が別トランザクション）。
// テストはその構造が壊れていないことを外から確かめる。
public class EmailIndependenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    // FR-22: **メール送信が失敗しても、アプリ内通知は残り、未読のまま読める**（AC-4）。
    [Fact]
    public async Task メール送信が失敗してもアプリ内通知は届いたまま残る()
    {
        using var h = new NotificationHarness();

        await h.Publisher().PublishAsync("alice", NotificationKinds.SyncTokenExpiry, Now,
            count: 2, deadline: Now.AddDays(7), ct: TestContext.Current.CancellationToken);

        var summary = await h.Dispatcher(new AlwaysFailingEmailTransport())
            .DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        summary.Failed.Should().Be(1);
        summary.Sent.Should().Be(0);

        var list = await h.Store().ListAsync("alice", ct: TestContext.Current.CancellationToken);
        list.Items.Should().ContainSingle("★ メールの失敗はアプリ内通知に触れられない");
        list.Items[0].Read.Should().BeFalse();
        list.UnreadCount.Should().Be(1);
    }

    // FR-22: **トランスポートが例外を投げても同じである**（AC-4）。
    // 例外が呼び出し元まで抜けると、後続の outbox が処理されないまま静かに滞留する。
    [Fact]
    public async Task メール送信が例外を投げてもアプリ内通知は残り送出は継続する()
    {
        using var h = new NotificationHarness();
        var publisher = h.Publisher();

        await publisher.PublishAsync("alice", NotificationKinds.PrivateNotePurgeDone, Now,
            count: 1, ct: TestContext.Current.CancellationToken);
        await publisher.PublishAsync("alice", NotificationKinds.StorageQuotaWarning, Now,
            thresholdPercent: 95, ct: TestContext.Current.CancellationToken);

        var summary = await h.Dispatcher(new ThrowingEmailTransport())
            .DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        summary.Failed.Should().Be(2, "★ 1 件目の例外で打ち切らず、2 件目も結末を得る");

        var list = await h.Store().ListAsync("alice", ct: TestContext.Current.CancellationToken);
        list.Items.Should().HaveCount(2);
        list.UnreadCount.Should().Be(2);
    }

    // FR-22, ADR-0045: **SMTP 未設定は「成功」ではなく `failed` である**（AC-4 ＋ AC-5）。
    // 「設定が無いから静かに何もしない」は、受け入れ基準が禁じている形そのものである。
    [Fact]
    public async Task SMTP未設定は成功ではなく失敗として記録される()
    {
        using var h = new NotificationHarness();

        await h.Publisher().PublishAsync("alice", NotificationKinds.PrivateNotePurgeWeekly, Now,
            count: 4, deadline: Now.AddDays(30), ct: TestContext.Current.CancellationToken);

        await h.Dispatcher(new UnconfiguredSmtpEmailTransport())
            .DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        var entry = await h.Db.EmailOutbox.SingleAsync(TestContext.Current.CancellationToken);
        entry.Status.Should().Be(EmailOutboxStatus.Failed);
        entry.LastReason.Should().Be(UnconfiguredSmtpEmailTransport.Reason);

        h.Audit.Records.Should().ContainSingle(r =>
            r.Action == EmailOutboxDispatcher.AuditActionPrefix + EmailOutboxStatus.Failed
            && r.Outcome == EmailOutboxStatus.Failed);
    }

    // FR-22: **宛先アドレスが解決できないのも「送れなかった」である**（AC-4 ＋ AC-5）。
    // 宛先未解決を no-op にすると、送れていない事実がどこにも残らない。
    [Fact]
    public async Task 宛先アドレスが解決できないと失敗として記録される()
    {
        using var h = new NotificationHarness();

        await h.Publisher().PublishAsync("alice", NotificationKinds.SyncTokenExpiry, Now,
            count: 1, deadline: Now.AddDays(7), ct: TestContext.Current.CancellationToken);

        await h.Dispatcher(new RecordingEmailTransport(), new UnresolvedEmailAddressResolver())
            .DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        var entry = await h.Db.EmailOutbox.SingleAsync(TestContext.Current.CancellationToken);
        entry.Status.Should().Be(EmailOutboxStatus.Failed);
        entry.LastReason.Should().Be(EmailOutboxDispatcher.ReasonNoAddress);

        var list = await h.Store().ListAsync("alice", ct: TestContext.Current.CancellationToken);
        list.Items.Should().ContainSingle("★ アプリ内通知は宛先解決に従属しない");
    }

    // FR-22, ADR-0037 決定 6: **メール本文にも件数と期限しか現れない**（AC-2 のメール側）。
    // メールは本システムの ABAC の外側へ出るため、この境界が最も重要である。
    [Fact]
    public async Task メール本文は件数と期限だけで組み立てられる()
    {
        using var h = new NotificationHarness();
        var transport = new RecordingEmailTransport();

        await h.Publisher().PublishAsync("alice", NotificationKinds.PrivateNotePurgeImminent, Now,
            count: 5, deadline: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            ct: TestContext.Current.CancellationToken);

        await h.Dispatcher(transport).DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        transport.Sent.Should().ContainSingle();
        transport.Sent[0].Body.Should().Be("個人資料 5 件が 2026-09-01 に完全削除されます（7 日前の通知）");
    }
}
