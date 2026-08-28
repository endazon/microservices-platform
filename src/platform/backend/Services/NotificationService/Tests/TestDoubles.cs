using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationService.Common.Observability;
using NotificationService.Common.Options;
using NotificationService.Infrastructure.Persistence;
using NotificationService.Features.Notifications;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace NotificationService.Tests;

// 監査ログを記録して読み返せるようにする（受け入れ基準「静かに落ちない」の検証に使う）。
public sealed class RecordingAuditLogger : IAuditLogger
{
    public List<(string Action, string Subject, string Outcome, string? Detail)> Records { get; } = [];

    public void Record(string action, string subject, string outcome, string? detail = null)
        => Records.Add((action, subject, outcome, detail));
}

// 送信が必ず失敗するトランスポート。**SMTP 不在・障害時の実際の振る舞いを再現する。**
public sealed class AlwaysFailingEmailTransport(string reason = "transport-down") : IEmailTransport
{
    public int Calls { get; private set; }

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(EmailSendResult.Failure(reason));
    }
}

// 例外を投げるトランスポート（失敗の表現が戻り値だけとは限らないため）。
public sealed class ThrowingEmailTransport : IEmailTransport
{
    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
        => throw new InvalidOperationException("smtp connection refused");
}

// 送信が必ず成功するトランスポート。送った本文を控えて内容を検証できるようにする。
public sealed class RecordingEmailTransport : IEmailTransport
{
    public List<EmailMessage> Sent { get; } = [];

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Sent.Add(message);
        return Task.FromResult(EmailSendResult.Success());
    }
}

public sealed class FixedEmailAddressResolver(string? address = "user@example.test") : IEmailAddressResolver
{
    public Task<string?> ResolveAsync(string subject, CancellationToken ct = default)
        => Task.FromResult(address);
}

// 単体レベルの器。InMemory の DbContext と、そこへ結線した各サービスを 1 か所で組み立てる。
public sealed class NotificationHarness : IDisposable
{
    public NotificationDbContext Db { get; }
    public RecordingAuditLogger Audit { get; } = new();
    public NotificationDeliveryMetrics Metrics { get; }
    public NotificationOptions Options { get; }

    public NotificationHarness(int dailyEmailLimit = 500, int retentionDays = 90)
    {
        Db = new NotificationDbContext(new DbContextOptionsBuilder<NotificationDbContext>()
            .UseInMemoryDatabase($"NotificationUnit_{Guid.NewGuid()}")
            .Options);
        Options = new NotificationOptions { DailyEmailLimit = dailyEmailLimit, RetentionDays = retentionDays };
        Metrics = new NotificationDeliveryMetrics(new DummyMeterFactory());
    }

    public IOptions<NotificationOptions> OptionsWrapper => Microsoft.Extensions.Options.Options.Create(Options);

    public NotificationStore Store() => new(Db, OptionsWrapper);

    public NotificationPublisher Publisher()
        => new(Db, Metrics, NullLogger<NotificationPublisher>.Instance);

    public EmailOutboxDispatcher Dispatcher(IEmailTransport transport, IEmailAddressResolver? resolver = null)
        => new(Db, transport, resolver ?? new FixedEmailAddressResolver(), Metrics, Audit,
               OptionsWrapper, NullLogger<EmailOutboxDispatcher>.Instance);

    public NotificationRetention Retention() => new(Db, OptionsWrapper);

    public void Dispose() => Db.Dispose();

    // IMeterFactory の最小実装（計器の生成だけができればよい）。
    private sealed class DummyMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var m in _meters) m.Dispose();
        }
    }
}
