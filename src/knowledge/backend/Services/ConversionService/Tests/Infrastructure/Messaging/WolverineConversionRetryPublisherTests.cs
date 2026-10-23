using System.Reflection;
using AwesomeAssertions;
using ConversionService.Infrastructure.Messaging;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.EntityFrameworkCore;
using Xunit;

namespace ConversionService.Tests.Infrastructure.Messaging;

// FR-12 例外フロー, ADR-0027 §決定, IADR-0609 (#2169): 再変換の発行口（Transactional Outbox）の呼び出しの約束。
// DocumentService.Tests の `WolverineDocumentUpdatedPublisherTests`（IADR-0608）と同じ 3 件を、本サービスの発行口で固定する。
//   - 確定の後の送出の失敗は例外にせず Warning で残し、DB の変更は確定している。
//     🔴 送出の失敗を素通しにする変異（catch を外す）で赤になる。
//   - `CommitAsync` の外で発行を積むと例外になる。🔴 門番（`_inCommit` の検査）を外す変異で赤になる。
//   - 関数（保存 → 積む）が例外で終われば、送出を試みず例外をそのまま出す。
//
// 確定（コミット）の失敗で積んだ発行が巻き戻ることは、実 PostgreSQL の `ConversionRetryOutboxTests`
// （Knowledge.IntegrationTests）が測る（InMemory にはトランザクションが無い）。
public class WolverineConversionRetryPublisherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // InMemory はトランザクションを持たない。発行口が開く・コミットするトランザクションは無視させる（本番の PostgreSQL では実体がある）。
    private static DbContextOptions<ConversionJobDbContext> InMemoryOptions() =>
        new DbContextOptionsBuilder<ConversionJobDbContext>()
            .UseInMemoryDatabase($"retry_publisher_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.pdf", $"storage://{id}/raw",
            "application/pdf", new Dictionary<string, string>(), [], DateTimeOffset.UtcNow);

    [Fact]
    public async Task 確定の後の送出の失敗は例外にせずWarningで残し変更は確定している()
    {
        var options = InMemoryOptions();
        await using var db = new ConversionJobDbContext(options);
        var outbox = FailingFlushOutbox.Create(db);
        var logger = new RecordingLogger<WolverineConversionRetryPublisher>();
        var publisher = new WolverineConversionRetryPublisher(outbox, logger);
        var store = new EfConversionJobStore(db);
        var ev = Raw(Guid.NewGuid());

        var act = () => publisher.CommitAsync(async c =>
        {
            await store.StartAsync(ev, c);
            await publisher.PublishRetryAsync(ev, c);
        }, Ct);

        await act.Should().NotThrowAsync("確定済みの再変換に失敗を返さない（イベントは Outbox の表から再送される）");
        FailingFlushOutbox.Of(outbox).FlushCalls.Should().Be(1, "送出は試みている");
        FailingFlushOutbox.Of(outbox).Published.OfType<RawDocumentFetched>().Should().ContainSingle(e => e.FetchId == ev.FetchId);
        await using (var reader = new ConversionJobDbContext(options))
            (await reader.ConversionJobs.AnyAsync(j => j.Id == ev.FetchId, Ct)).Should().BeTrue("DB の変更は確定している");
        logger.Warnings.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task 確定の外で発行を積むと例外になる()
    {
        await using var db = new ConversionJobDbContext(InMemoryOptions());
        var outbox = FailingFlushOutbox.Create(db);
        var publisher = new WolverineConversionRetryPublisher(outbox, new RecordingLogger<WolverineConversionRetryPublisher>());
        var ev = Raw(Guid.NewGuid());

        var outside = () => publisher.PublishRetryAsync(ev, Ct);

        await outside.Should().ThrowAsync<InvalidOperationException>().WithMessage("*CommitAsync*");
        FailingFlushOutbox.Of(outbox).Published.Should().BeEmpty("Outbox へは何も積まれていない");

        // 一度確定した後も、確定の外では積めない（門番は確定のたびに閉じる）。
        await publisher.CommitAsync(c => publisher.PublishRetryAsync(ev, c), Ct);
        await outside.Should().ThrowAsync<InvalidOperationException>();
        FailingFlushOutbox.Of(outbox).Published.Should().ContainSingle();
    }

    [Fact]
    public async Task 保存が失敗すれば送出を試みず例外をそのまま出す()
    {
        await using var db = new ConversionJobDbContext(InMemoryOptions());
        var outbox = FailingFlushOutbox.Create(db);
        var publisher = new WolverineConversionRetryPublisher(outbox, new RecordingLogger<WolverineConversionRetryPublisher>());
        var ev = Raw(Guid.NewGuid());

        var act = () => publisher.CommitAsync(async c =>
        {
            await publisher.PublishRetryAsync(ev, c);
            throw new DbUpdateException("保存の失敗（試験の注入）");
        }, Ct);

        await act.Should().ThrowAsync<DbUpdateException>();
        FailingFlushOutbox.Of(outbox).FlushCalls.Should().Be(0, "確定しなかった再変換のイベントは送らない");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<Exception?> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(exception);
        }
    }

    // 送出（FlushOutgoingMessagesAsync）だけが失敗する Outbox。`IDbContextOutbox<T>` は IMessageBus の全面を持つため、
    // 使う 3 つ（DbContext・PublishAsync・FlushOutgoingMessagesAsync）以外は呼ばれたら失敗させる。
    public class FailingFlushOutbox : DispatchProxy
    {
        public ConversionJobDbContext Db { get; private set; } = null!;
        public List<object> Published { get; } = [];
        public int FlushCalls { get; private set; }

        public static IDbContextOutbox<ConversionJobDbContext> Create(ConversionJobDbContext db)
        {
            var proxy = Create<IDbContextOutbox<ConversionJobDbContext>, FailingFlushOutbox>();
            ((FailingFlushOutbox)(object)proxy).Db = db;
            return proxy;
        }

        public static FailingFlushOutbox Of(IDbContextOutbox<ConversionJobDbContext> outbox) => (FailingFlushOutbox)(object)outbox;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_DbContext":
                    return Db;
                case "PublishAsync":
                    Published.Add(args![0]!);
                    return ValueTask.CompletedTask;
                case "FlushOutgoingMessagesAsync":
                    FlushCalls++;
                    return Task.FromException(new InvalidOperationException("ブローカへ送れなかった（試験の注入）"));
                default:
                    throw new NotSupportedException($"{targetMethod?.Name} は使われない想定である。");
            }
        }
    }
}
