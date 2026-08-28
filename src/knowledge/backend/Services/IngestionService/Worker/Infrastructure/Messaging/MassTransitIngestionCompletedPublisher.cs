using IngestionService.Worker.Domain.Ports;
using Knowledge.Contracts.Events;
using MassTransit;

namespace IngestionService.Worker.Infrastructure.Messaging;

// FR-02 / ADR-0003（Superseded by ADR-0027・注記は #580）: IngestionCompleted の発行。
//
// 🔴 **本ファイルには `using Wolverine;` を入れてはならない。**
// `check-event-topology.js` の `transportsOfFile` はファイル中の `using` からトランスポートを
// 導出する。両方が同居すると、この発行が `masstransit+wolverine` の両方として記録され、
// `transportMismatches()` の発行側 union に wolverine が混ざる —— IngestionCompleted の辺を
// Wolverine へ移すとき違反が報告されなくなる（IADR-0245 方向 1 の地雷。E1 の
// MassTransitDocumentNormalizedPublisher と同じ作法）。
//
// **辺 IngestionCompleted は本 PR（E3b）の射程外である。** トランスポートは変えない ——
// 変えるのは「どのファイルに置くか」だけである。
public sealed class MassTransitIngestionCompletedPublisher(IPublishEndpoint bus)
    : IIngestionCompletedPublisher
{
    public Task PublishCompletedAsync(
        Guid documentId, int chunkCount, DateTimeOffset completedAt, CancellationToken ct = default) =>
        // 🔴 イベントの構築をここに置くのは、`findPublishers` が `Publish(new <Event>(` にしか
        // 一致しないためである（組み立て済みの変数を渡すと発行が検査器から見えなくなる）。
        bus.Publish(new IngestionCompleted(documentId, chunkCount, completedAt), ct);
}
