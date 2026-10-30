using DataSourceService.Domain.Ports;
using Knowledge.Contracts.Events;

namespace DataSourceService.Tests;

// FR-01, UC-04, IADR-0657 (#2361): 発行口 `IRawDocumentFetchedPublisher` のテストダブル。
// 発行を `RecordingMessageBus` へ記録する —— 既存の試験の観測点（`bus.PublishedOf<RawDocumentFetched>()`）をそのまま使うためである。
// ⚠️ 送出表へのコミット（本番の `WolverineRawDocumentFetchedPublisher`）は本ダブルでは測れない。測るのは
// Knowledge.IntegrationTests の `RawDocumentFetchedStoreFailureTests`（実 PostgreSQL）である。
public sealed class RecordingRawDocumentFetchedPublisher(RecordingMessageBus bus) : IRawDocumentFetchedPublisher
{
    public async Task PublishAsync(RawDocumentFetched ev, CancellationToken ct = default) =>
        await bus.PublishAsync(ev);
}
