using IngestionService.Domain.Ports;
using Knowledge.Contracts.Events;
using Wolverine;

namespace IngestionService.Infrastructure.Messaging;

// FR-02, ADR-0027, IADR-0234 単位 E4 (#2134): IngestionCompleted の発行（Wolverine）。経路は `Program.cs` の
// `RoutePlatformEvent<IngestionCompleted>()`（RabbitMQ の fan-out exchange `IngestionCompleted`）。
//
// 購読者は無く、結線もしない（planning#741 項目 6 の裁定・2026-10-09）。exchange に束ねたキューが無いので、ブローカが捨てる。
// 🔴 **発行は非耐久である。** IngestionService は DB を持たず、Transactional Outbox も耐久送出も使えない（送出の前にプロセスが
// 落ちると失われる）。購読者が付く時点で耐久化を再検討する（作業仕様書 20261025_2134 §発行は非耐久である）。
//
// ⚠️ イベントの構築はここに置く —— `check-event-topology.js` の `findPublishers` は `Publish*(new <Event>(` にしか一致しない。
public sealed class WolverineIngestionCompletedPublisher(IMessageBus bus) : IIngestionCompletedPublisher
{
    public Task PublishCompletedAsync(
        Guid documentId, int chunkCount, DateTimeOffset completedAt, CancellationToken ct = default) =>
        // `ct` はここでは効かない: Wolverine の `IMessageBus.PublishAsync` は取り消しを受け取らない（ポートの形を他の発行口と揃えるために残す）。
        bus.PublishAsync(new IngestionCompleted(documentId, chunkCount, completedAt)).AsTask();
}
