using System.Reflection;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Messaging;
using DocumentService.Infrastructure.Persistence;
using DocumentService.Tests.Infrastructure.ExternalServices;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.EntityFrameworkCore;

namespace DocumentService.Tests.Infrastructure.Messaging;

// FR-06, ADR-0027 §決定, IADR-0608 決定 2 (#2168): 更新イベントの発行口（Transactional Outbox）の呼び出しの約束。
//
// ここでは本番の発行口に「送出だけが失敗する Outbox」を渡して、次を固定する。
//   - 確定の後の送出の失敗は例外にせず Warning で残し、DB の変更は確定している（IADR-0604 決定 2 と同じ扱い）。
//     🔴 送出の失敗を素通しにする変異（catch を外す）で赤になる。
//   - `CommitAsync` の外で発行を積むと例外になる（確定されないまま捨てられる発行を作らない）。
//     🔴 門番（`_inCommit` の検査）を外す変異で赤になる。
//   - 関数（保存 → 積む）が例外で終われば、送出を試みず例外をそのまま出す。
//
// 確定（コミット）の失敗で積んだ発行が巻き戻ることは、実 PostgreSQL の `DocumentUpdatedOutboxTests`
// （Knowledge.IntegrationTests）が測る（InMemory にはトランザクションが無い）。
public class WolverineDocumentUpdatedPublisherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // InMemory はトランザクションを持たない。発行口が開く・コミットするトランザクションは無視させる（本番の PostgreSQL では実体がある）。
    private static DbContextOptions<DocumentDbContext> InMemoryOptions() =>
        new DbContextOptionsBuilder<DocumentDbContext>()
            .UseInMemoryDatabase($"updated_publisher_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static Document NewDocument(Guid id) =>
        Document.CreateNormalized(id, "更新する文書", $"storage://knowledge-normalized/{id:N}/document.md",
            new Dictionary<string, string> { ["confidentiality"] = "internal" });

    private static Task PublishAsync(WolverineDocumentUpdatedPublisher publisher, Document d, CancellationToken ct) =>
        publisher.PublishUpdatedAsync(d.Id, d.Title, d.Status, d.MarkdownUri, d.Attributes, [], d.UpdatedAt, ct: ct);

    [Fact]
    public async Task 確定の後の送出の失敗は例外にせずWarningで残し更新は確定している()
    {
        var options = InMemoryOptions();
        await using var db = new DocumentDbContext(options);
        var outbox = FailingFlushOutbox.Create(db);
        var logger = new RecordingLogger<WolverineDocumentUpdatedPublisher>();
        var publisher = new WolverineDocumentUpdatedPublisher(outbox, logger);
        var doc = NewDocument(Guid.NewGuid());

        db.Documents.Add(doc);
        var act = () => publisher.CommitAsync(async c =>
        {
            await db.SaveChangesAsync(c);
            await PublishAsync(publisher, doc, c);
        }, Ct);

        await act.Should().NotThrowAsync("確定済みの更新に失敗を返さない（イベントは Outbox の表から再送される）");
        FailingFlushOutbox.Of(outbox).FlushCalls.Should().Be(1, "送出は試みている");
        FailingFlushOutbox.Of(outbox).Published.OfType<DocumentUpdated>().Should().ContainSingle(e => e.DocumentId == doc.Id);
        await using (var reader = new DocumentDbContext(options))
            (await reader.Documents.AnyAsync(d => d.Id == doc.Id, Ct)).Should().BeTrue("DB の変更は確定している");
        logger.OfLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task 確定の外で発行を積むと例外になる()
    {
        await using var db = new DocumentDbContext(InMemoryOptions());
        var outbox = FailingFlushOutbox.Create(db);
        var publisher = new WolverineDocumentUpdatedPublisher(outbox, new RecordingLogger<WolverineDocumentUpdatedPublisher>());
        var doc = NewDocument(Guid.NewGuid());

        var outside = () => PublishAsync(publisher, doc, Ct);

        await outside.Should().ThrowAsync<InvalidOperationException>().WithMessage("*CommitAsync*");
        FailingFlushOutbox.Of(outbox).Published.Should().BeEmpty("Outbox へは何も積まれていない");

        // 一度確定した後も、確定の外では積めない（門番は確定のたびに閉じる）。
        await publisher.CommitAsync(c => PublishAsync(publisher, doc, c), Ct);
        await outside.Should().ThrowAsync<InvalidOperationException>();
        FailingFlushOutbox.Of(outbox).Published.Should().ContainSingle();
    }

    [Fact]
    public async Task 保存が失敗すれば送出を試みず例外をそのまま出す()
    {
        await using var db = new DocumentDbContext(InMemoryOptions());
        var outbox = FailingFlushOutbox.Create(db);
        var publisher = new WolverineDocumentUpdatedPublisher(outbox, new RecordingLogger<WolverineDocumentUpdatedPublisher>());
        var doc = NewDocument(Guid.NewGuid());

        var act = () => publisher.CommitAsync(async c =>
        {
            await PublishAsync(publisher, doc, c);
            throw new DbUpdateException("保存の失敗（試験の注入）");
        }, Ct);

        await act.Should().ThrowAsync<DbUpdateException>();
        FailingFlushOutbox.Of(outbox).FlushCalls.Should().Be(0, "確定しなかった更新のイベントは送らない");
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
