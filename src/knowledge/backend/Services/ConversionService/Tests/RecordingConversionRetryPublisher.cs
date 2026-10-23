using ConversionService.Domain.Ports;
using Knowledge.Contracts.Events;

namespace ConversionService.Tests;

// FR-12 例外フロー, ADR-0027 §決定, IADR-0609 (#2169): 試験の器の再変換の発行口（Outbox の代役）。
//
// 本番の `WolverineConversionRetryPublisher` は `IDbContextOutbox<ConversionJobDbContext>` を使い、envelope を
// DbContext の**関係データベースの**トランザクションへ INSERT する。器の DB は InMemory なので本物は使えない
// （トランザクションも Outbox の表も無い）。原子性そのものは Knowledge.IntegrationTests の
// `ConversionRetryOutboxTests` が実 PostgreSQL で測る。
//
// ここで守るのは**呼び出し側の約束**である（本番の発行口と同じ形で破れる。DocumentService.Tests の
// `RecordingDocumentUpdatedPublisher` と同じ作法）:
//   - 積めるのは `CommitAsync` の関数の中だけ（外で積むと例外。本番と同じ）。
//   - 積んだ発行は関数（保存 → 発行を積む）が**成功した後でだけ** `RecordingMessageBus` へ出る。保存が例外で終われば 1 通も出ない。
public sealed class RecordingConversionRetryPublisher(RecordingMessageBus bus) : IConversionRetryPublisher
{
    private List<RawDocumentFetched>? _pending;

    public Task PublishRetryAsync(RawDocumentFetched ev, CancellationToken ct = default)
    {
        if (_pending is null)
            throw new InvalidOperationException(
                "再変換の RawDocumentFetched は IConversionRetryPublisher.CommitAsync の関数の中でだけ積める（IADR-0609）。");
        _pending.Add(ev);
        return Task.CompletedTask;
    }

    public async Task CommitAsync(Func<CancellationToken, Task> saveAndPublish, CancellationToken ct = default)
    {
        if (_pending is not null)
            throw new InvalidOperationException("IConversionRetryPublisher.CommitAsync は入れ子にできない（IADR-0609）。");
        _pending = [];
        List<RawDocumentFetched> pending;
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
