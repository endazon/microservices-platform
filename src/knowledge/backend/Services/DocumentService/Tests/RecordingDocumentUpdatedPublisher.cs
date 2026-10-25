using DocumentService.Domain.Ports;
using Knowledge.Contracts.Events;

namespace DocumentService.Tests;

// FR-06, ADR-0027 §決定, IADR-0608 (#2168): 試験の器の更新イベントの発行口（Outbox の代役）。
//
// 本番の `WolverineDocumentUpdatedPublisher` は `IDbContextOutbox<DocumentDbContext>` を使い、envelope を
// DbContext の**関係データベースの**トランザクションへ INSERT する。器の DB は InMemory なので本物は使えない
// （トランザクションも Outbox の表も無い）。原子性そのものは Knowledge.IntegrationTests の
// `DocumentUpdatedOutboxTests` が実 PostgreSQL で測る。
//
// ここで守るのは**呼び出し側の約束**である（本番の発行口と同じ形で破れる）:
//   - 積めるのは `CommitAsync` の関数の中だけ（外で積むと例外。本番と同じ）。
//   - 積んだ発行は関数（保存 → 発行を積む）が**成功した後でだけ** `RecordingMessageBus` へ出る。保存が例外で終われば 1 通も出ない。
// 端点が確定の外で発行すれば例外になり、既存の「更新で DocumentUpdated が出る」試験が赤になる。
public sealed class RecordingDocumentUpdatedPublisher(RecordingMessageBus bus) : IDocumentUpdatedPublisher
{
    private List<DocumentUpdated>? _pending;

    public Task PublishUpdatedAsync(
        Guid documentId, string title, string status, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags, DateTimeOffset updatedAt,
        string? contentFingerprint = null, bool hasBody = true,
        string? originalPath = null, string? dataSourceName = null,
        List<string>? sharedWith = null, long? sequence = null, CancellationToken ct = default)
    {
        if (_pending is null)
            throw new InvalidOperationException(
                "DocumentUpdated は IDocumentUpdatedPublisher.CommitAsync の関数の中でだけ積める（IADR-0608 決定 2）。");
        _pending.Add(new DocumentUpdated(documentId, title, status, markdownUri, attributes, tags, updatedAt,
            contentFingerprint, hasBody, originalPath, dataSourceName, sharedWith, sequence));
        return Task.CompletedTask;
    }

    public async Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct = default)
    {
        if (_pending is not null)
            throw new InvalidOperationException("IDocumentUpdatedPublisher.CommitAsync は入れ子にできない（IADR-0608 決定 2）。");
        _pending = [];
        List<DocumentUpdated> pending;
        try
        {
            // 確定が例外で終われば、積んだ発行は捨てられる（本番の巻き戻しと同じ向き）。
            await saveAndPublish(ct);
            pending = _pending;
        }
        finally
        {
            _pending = null;
        }
        foreach (var e in pending)
            await bus.PublishAsync(e);
    }
}
