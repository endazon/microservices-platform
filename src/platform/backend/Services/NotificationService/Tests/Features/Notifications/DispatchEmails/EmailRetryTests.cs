using System.Diagnostics.Metrics;
using System.Net.Sockets;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NotificationService.Common.Observability;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.DispatchEmails;

namespace NotificationService.Tests.Features.Notifications.DispatchEmails;

// FR-22, ADR-0045 決定 8・§結果 フォローアップ 6, IADR-0600: 一過性の送信失敗の上限つき再送（T-40）。
//
// ★ 確かめるのは 4 点である（#2113 の受け入れ基準）。
//   1. 一過性の失敗は `deferred` に戻り、次の周期で再送される
//   2. 上限を超えたものだけが `failed` になり、計器 notification.email.retry_exhausted が 1 増える
//   3. 恒久的な失敗・SMTP 未構成は即 `failed` で、再試行されない（毎周期の再送が起きない）
//   4. バックオフの次回時刻より前には拾わない
[Trait("TestKind", "Unit")]
public class EmailRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Base = TimeSpan.FromMinutes(5);

    // 計器の観測は種別で切り分ける（kind の値域は開いている）。並行する他の試験の計器と混ざらない。
    private static string UniqueKind() => "retry-test-" + Guid.NewGuid().ToString("N");

    private static async Task PublishAsync(NotificationHarness h, string kind, DateTimeOffset? deadline = null)
        => await h.Publisher().PublishAsync("alice", kind, Now.AddMinutes(-1),
            count: 1, deadline: deadline ?? Now.AddDays(30), ct: TestContext.Current.CancellationToken);

    private static Task<EmailOutboxEntry> EntryAsync(NotificationHarness h)
        => h.Db.EmailOutbox.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

    // 基準 1（FR-22, IADR-0600 決定 1・2）: 一過性の失敗は deferred に戻り、次の周期で送られる。
    [Fact]
    public async Task 一過性の失敗はdeferredに戻り次の周期で再送される()
    {
        using var h = new NotificationHarness();
        await PublishAsync(h, NotificationKinds.SyncTokenExpiry);
        var transport = new TransientThenSucceedingEmailTransport(failures: 1);
        var dispatcher = h.Dispatcher(transport);

        var first = await dispatcher.DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        first.Failed.Should().Be(0, "★ 一過性の失敗を終端の failed にしない");
        first.Deferred.Should().Be(1);
        first.RetryScheduled.Should().Be(1);
        var entry = await EntryAsync(h);
        entry.Status.Should().Be(EmailOutboxStatus.Deferred);
        entry.AttemptCount.Should().Be(1);
        entry.DeferralCount.Should().Be(0, "上限の繰り越しとは数えを分ける");
        entry.NextAttemptAt.Should().Be(Now + Base);
        entry.LastReason.Should().Be(EmailOutboxDispatcher.ReasonTransientPrefix + "smtp-4xx");
        h.Audit.Records.Should().ContainSingle(r =>
            r.Action == EmailOutboxDispatcher.AuditActionPrefix + EmailOutboxStatus.Deferred
            && r.Detail!.Contains("reason=transient:smtp-4xx"));

        var second = await dispatcher.DispatchPendingAsync(Now + Base, TestContext.Current.CancellationToken);

        second.Sent.Should().Be(1, "★ 次の周期で再送される");
        transport.Calls.Should().Be(2);
        var sent = await EntryAsync(h);
        sent.Status.Should().Be(EmailOutboxStatus.Sent);
        sent.AttemptCount.Should().Be(2);
        sent.NextAttemptAt.Should().BeNull();
    }

    // 基準 1（例外の経路）: 接続断・時間切れの例外は一過性として扱う。
    [Theory]
    [InlineData("timeout")]
    [InlineData("io")]
    [InlineData("socket")]
    [InlineData("canceled")]
    public async Task 接続断と時間切れの例外は一過性として再試行に回る(string kind)
    {
        using var h = new NotificationHarness();
        await PublishAsync(h, NotificationKinds.SyncTokenExpiry);
        Func<Exception> factory = kind switch
        {
            "timeout" => () => new TimeoutException("smtp timed out"),
            "io" => () => new IOException("connection reset"),
            "socket" => () => new SocketException((int)SocketError.ConnectionRefused),
            _ => () => new TaskCanceledException("http timeout"),
        };

        var summary = await h.Dispatcher(new ExceptionEmailTransport(factory))
            .DispatchPendingAsync(Now, TestContext.Current.CancellationToken);

        summary.RetryScheduled.Should().Be(1);
        (await EntryAsync(h)).Status.Should().Be(EmailOutboxStatus.Deferred);
    }

    // 基準 2（FR-22, IADR-0600 決定 3）: 上限を超えたものだけが failed になり、計器が 1 増える。
    [Fact]
    public async Task 試行の上限を超えたものだけがfailedになり計器が1増える()
    {
        using var h = new NotificationHarness();
        h.Options.EmailMaxSendAttempts = 3;
        var kind = UniqueKind();
        await PublishAsync(h, kind);
        var transport = new TransientThenSucceedingEmailTransport(failures: int.MaxValue);
        var dispatcher = h.Dispatcher(transport);
        using var exhausted = new RetryExhaustedListener(kind);

        // 1 回目・2 回目の失敗: まだ上限に達していない。failed にせず、計器も動かない。
        var at = Now;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var s = await dispatcher.DispatchPendingAsync(at, TestContext.Current.CancellationToken);
            s.Failed.Should().Be(0, $"{attempt} 回目はまだ上限内である");
            exhausted.Count.Should().Be(0);
            var e = await EntryAsync(h);
            e.Status.Should().Be(EmailOutboxStatus.Deferred);
            at = e.NextAttemptAt!.Value;
        }

        // 3 回目の失敗: 上限（3 回）に達したので、ここで初めて failed。
        var last = await dispatcher.DispatchPendingAsync(at, TestContext.Current.CancellationToken);

        last.Failed.Should().Be(1);
        last.RetryExhausted.Should().Be(1);
        exhausted.Count.Should().Be(1, "★ 上限超過は計器で 1 件として数える");
        var entry = await EntryAsync(h);
        entry.Status.Should().Be(EmailOutboxStatus.Failed);
        entry.AttemptCount.Should().Be(3);
        entry.NextAttemptAt.Should().BeNull();
        entry.LastReason.Should().Be(EmailOutboxDispatcher.ReasonRetryExhaustedPrefix + "smtp-4xx");

        // 終端である: 以後の周期では拾わない。
        await dispatcher.DispatchPendingAsync(at.AddDays(1), TestContext.Current.CancellationToken);
        transport.Calls.Should().Be(3);
        exhausted.Count.Should().Be(1);
    }

    // 基準 3（FR-22, IADR-0215 決定 3, IADR-0600 決定 1）: 恒久的な失敗・未構成は即 failed で再試行されない。
    [Theory]
    [InlineData("unconfigured")]
    [InlineData("permanent-result")]
    [InlineData("unknown-exception")]
    public async Task 恒久的な失敗と未構成は即failedで以後の周期に再送されない(string shape)
    {
        using var h = new NotificationHarness();
        var kind = UniqueKind();
        await PublishAsync(h, kind);
        using var exhausted = new RetryExhaustedListener(kind);

        var calls = 0;
        IEmailTransport transport = shape switch
        {
            "unconfigured" => new CountingTransport(new UnconfiguredSmtpEmailTransport(), () => calls++),
            "permanent-result" => new CountingTransport(new AlwaysFailingEmailTransport("recipient-rejected"), () => calls++),
            _ => new CountingTransport(new ThrowingEmailTransport(), () => calls++),
        };
        var dispatcher = h.Dispatcher(transport);

        var first = await dispatcher.DispatchPendingAsync(Now, TestContext.Current.CancellationToken);
        first.Failed.Should().Be(1);
        first.RetryScheduled.Should().Be(0);
        (await EntryAsync(h)).Status.Should().Be(EmailOutboxStatus.Failed);

        // ★ 毎周期の再送が起きない（未構成のまま送り続けない）。
        for (var i = 1; i <= 3; i++)
            await dispatcher.DispatchPendingAsync(Now + Base * i * 12, TestContext.Current.CancellationToken);

        calls.Should().Be(1, "★ 恒久的な失敗は 1 回で終わる");
        exhausted.Count.Should().Be(0, "再試行を使い切ったのではない（計器を混ぜない）");
        h.Audit.Records.Count(r => r.Action == EmailOutboxDispatcher.AuditActionPrefix + EmailOutboxStatus.Failed)
            .Should().Be(1);
    }

    // 基準 3（宛先未解決）: 送信を試みずに即 failed で、再試行もしない。
    [Fact]
    public async Task 宛先未解決は即failedで再試行されない()
    {
        using var h = new NotificationHarness();
        await PublishAsync(h, NotificationKinds.SyncTokenExpiry);
        var transport = new TransientThenSucceedingEmailTransport(failures: 0);
        var dispatcher = h.Dispatcher(transport, new FixedEmailAddressResolver(null));

        await dispatcher.DispatchPendingAsync(Now, TestContext.Current.CancellationToken);
        await dispatcher.DispatchPendingAsync(Now.AddHours(1), TestContext.Current.CancellationToken);

        (await EntryAsync(h)).Status.Should().Be(EmailOutboxStatus.Failed);
        transport.Calls.Should().Be(0);
    }

    // 基準 4（FR-22, IADR-0600 決定 2）: バックオフの次回時刻より前には拾わない。待ちは倍々に伸び、上限で頭打ち。
    [Fact]
    public async Task バックオフの次回時刻より前には拾わず待ちは倍々に伸びる()
    {
        using var h = new NotificationHarness();
        h.Options.EmailMaxSendAttempts = 10;
        h.Options.EmailRetryMaxDelay = TimeSpan.FromMinutes(15);
        await PublishAsync(h, NotificationKinds.SyncTokenExpiry);
        var transport = new TransientThenSucceedingEmailTransport(failures: int.MaxValue);
        var dispatcher = h.Dispatcher(transport);

        await dispatcher.DispatchPendingAsync(Now, TestContext.Current.CancellationToken);
        var next = (await EntryAsync(h)).NextAttemptAt!.Value;
        next.Should().Be(Now + Base);

        // 次回時刻の直前: 拾わない（送らない・結末を記録しない）。
        var early = await dispatcher.DispatchPendingAsync(next - TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        early.Sent.Should().Be(0);
        early.Deferred.Should().Be(0);
        early.Failed.Should().Be(0);
        transport.Calls.Should().Be(1, "★ 次回時刻より前には送信を試みない");

        // 次回時刻ちょうど: 拾う。2 回目の失敗の待ちは 2 倍（10 分）。
        await dispatcher.DispatchPendingAsync(next, TestContext.Current.CancellationToken);
        transport.Calls.Should().Be(2);
        var second = (await EntryAsync(h)).NextAttemptAt!.Value;
        second.Should().Be(next + Base * 2);

        // 3 回目の失敗: 本来 20 分だが上限 15 分で頭打ち。
        await dispatcher.DispatchPendingAsync(second, TestContext.Current.CancellationToken);
        (await EntryAsync(h)).NextAttemptAt.Should().Be(second + TimeSpan.FromMinutes(15));
    }

    // 再試行待ちでも期限は効く: 期限を過ぎた再試行待ちは dropped として記録して捨てる（IADR-0215 決定 4 と同じ）。
    [Fact]
    public async Task 期限を過ぎた再試行待ちは理由つきで破棄される()
    {
        using var h = new NotificationHarness();
        await PublishAsync(h, NotificationKinds.PrivateNotePurgeImminent, deadline: Now + TimeSpan.FromMinutes(3));
        var dispatcher = h.Dispatcher(new TransientThenSucceedingEmailTransport(failures: int.MaxValue));

        await dispatcher.DispatchPendingAsync(Now, TestContext.Current.CancellationToken);
        var summary = await dispatcher.DispatchPendingAsync(Now + Base, TestContext.Current.CancellationToken);

        summary.Dropped.Should().Be(1);
        (await EntryAsync(h)).LastReason.Should().Be(EmailOutboxDispatcher.ReasonDeadlinePassed);
    }

    private sealed class CountingTransport(IEmailTransport inner, Action onCall) : IEmailTransport
    {
        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            onCall();
            return inner.SendAsync(message, ct);
        }
    }

    // notification.email.retry_exhausted を、指定の種別に限って数える。
    private sealed class RetryExhaustedListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _count;

        public RetryExhaustedListener(string kind)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == NotificationDeliveryMetrics.MeterName
                    && instrument.Name == NotificationDeliveryMetrics.RetryExhaustedCounterName)
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == NotificationDeliveryMetrics.KindTag && (string?)tag.Value == kind)
                        Interlocked.Add(ref _count, value);
                }
            });
            _listener.Start();
        }

        public long Count => Interlocked.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }
}
