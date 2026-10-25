using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;

namespace DocumentService.Tests;

// FR-06, FR-19, ADR-0027 §決定, IADR-0604 (#2042): 試験の器の削除イベントの発行口（Outbox の代役）。
//
// 本番の `WolverineDocumentDeletedPublisher` は `IDbContextOutbox<DocumentDbContext>` を使い、envelope を
// DbContext の**関係データベースの**トランザクションへ INSERT する。器の DB は InMemory なので本物は使えない
// （トランザクションも Outbox の表も無い）。原子性そのものは Knowledge.IntegrationTests の
// `DocumentDeletedOutboxTests` が実 PostgreSQL で測る。
//
// ここで守るのは**呼び出し側の約束**である: 積んだ発行は `SaveChangesAndFlushAsync` が**確定に成功した後でだけ**
// `RecordingMessageBus` へ出る。確定を呼ばない経路・確定が例外で終わった経路では 1 通も出ない。
// 端点が確定を `DocumentDbContext.SaveChangesAsync` の直呼びへ戻すと、既存の「削除で DocumentDeleted が出る」試験が赤になる。
public sealed class RecordingDocumentDeletedPublisher(DocumentDbContext db, RecordingMessageBus bus)
    : IDocumentDeletedPublisher
{
    private readonly List<DocumentDeleted> _pending = [];

    public Task PublishDeletedAsync(Guid documentId, DateTimeOffset deletedAt, long? sequence = null, CancellationToken ct = default)
    {
        _pending.Add(new DocumentDeleted(documentId, deletedAt, sequence));
        return Task.CompletedTask;
    }

    public async Task SaveChangesAndFlushAsync(CancellationToken ct = default)
    {
        var pending = _pending.ToList();
        _pending.Clear();
        // 確定が例外で終われば、積んだ発行は捨てられる（本番の巻き戻しと同じ向き）。
        await db.SaveChangesAsync(ct);
        foreach (var e in pending)
            await bus.PublishAsync(e);
    }
}
