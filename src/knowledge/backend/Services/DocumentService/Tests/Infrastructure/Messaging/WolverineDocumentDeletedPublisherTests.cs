using System.Reflection;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Messaging;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Infrastructure.ExternalServices;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wolverine.EntityFrameworkCore;

namespace DocumentService.Tests.Infrastructure.Messaging;

// FR-06, FR-19, ADR-0027 §決定, IADR-0604 決定 2 (#2042): **確定の後の送出の失敗は呼び出し側へ出さない。**
//
// 削除は確定済みで、送れなかったイベントは Outbox の表に残り耐久エージェントが再送する。例外を出すと、確定済みの削除に
// 500 を返し、利用者がやり直すと 404 になる。ここでは本番の発行口に「送出だけが失敗する Outbox」を渡して、
// ①例外が出ない ②DB の変更は確定している ③失敗が Warning で残る、を固定する。
// 🔴 送出の失敗を素通しにする変異（catch を外す）で ① が赤になる。
//
// 確定（コミット）の失敗が例外のまま出て、積んだ発行が巻き戻ることは、実 PostgreSQL の
// `DocumentDeletedOutboxTests`（Knowledge.IntegrationTests）が測る（InMemory にはトランザクションが無い）。
public class WolverineDocumentDeletedPublisherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 確定の後の送出の失敗は例外にせずWarningで残し削除は確定している()
    {
        var options = new DbContextOptionsBuilder<DocumentDbContext>()
            .UseInMemoryDatabase($"publisher_{Guid.NewGuid():N}").Options;
        await using var db = new DocumentDbContext(options);
        var outbox = FailingFlushOutbox.Create(db);
        var logger = new RecordingLogger<WolverineDocumentDeletedPublisher>();
        var publisher = new WolverineDocumentDeletedPublisher(outbox, logger);
        var id = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        db.DocumentDeletionMarks.Add(DocumentDeletionMark.For(id, at));
        await publisher.PublishDeletedAsync(id, at, ct: Ct);
        var act = () => publisher.SaveChangesAndFlushAsync(Ct);

        await act.Should().NotThrowAsync("確定済みの削除に失敗を返さない（イベントは Outbox の表から再送される）");
        FailingFlushOutbox.Of(outbox).FlushCalls.Should().Be(1, "送出は試みている");
        FailingFlushOutbox.Of(outbox).Published.OfType<DocumentDeleted>().Should().ContainSingle(e => e.DocumentId == id);
        await using (var reader = new DocumentDbContext(options))
            (await reader.DocumentDeletionMarks.AnyAsync(m => m.DocumentId == id, Ct)).Should().BeTrue("DB の変更は確定している");
        logger.OfLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    // 送出（FlushOutgoingMessagesAsync）だけが失敗する Outbox。`IDbContextOutbox<T>` は IMessageBus の全面を持つため、
    // 使う 3 つ（DbContext・PublishAsync・FlushOutgoingMessagesAsync）以外は呼ばれたら失敗させる。
    public class FailingFlushOutbox : DispatchProxy
    {
        public DocumentDbContext Db { get; private set; } = null!;
        public List<object> Published { get; } = [];
        public int FlushCalls { get; private set; }

        public static IDbContextOutbox<DocumentDbContext> Create(DocumentDbContext db)
        {
            var proxy = Create<IDbContextOutbox<DocumentDbContext>, FailingFlushOutbox>();
            ((FailingFlushOutbox)(object)proxy).Db = db;
            return proxy;
        }

        public static FailingFlushOutbox Of(IDbContextOutbox<DocumentDbContext> outbox) => (FailingFlushOutbox)(object)outbox;

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
