using DocumentService.Application.Foundation.Ports;
using Knowledge.Contracts.Events;
using Wolverine;

namespace DocumentService.Api.Composable.Adapters;

// FR-06 / ADR-0027（E3a）: DocumentDeleted の発行（Wolverine）。
//
// 🔴 **本ファイルには `using MassTransit;` を入れてはならない。**
// `check-event-topology.js` の `transportsOfFile` はファイル中の `using` からトランスポートを
// 導出する。両方が同居すると、この発行が `masstransit+wolverine` の両方として記録され、
// `transportMismatches()` の発行側 union に masstransit が混ざる（逆行の隠蔽）。
// 1 ファイル 1 トランスポートを保つ（E1 の MassTransitDocumentNormalizedPublisher と同じ作法）。
//
// 配置は IADR-0280 の写像では Adapters → Infrastructure だが、トランスポート実装は
// 合成ルート（Api）が持つ Platform.Shared.Infrastructure / WolverineFx.RuntimeCompilation に
// 依存するため、messaging パッケージを Infrastructure 骨格へ持ち込まず Api 側へ置く
// （判断の記録は作業仕様書 20260828_edge-e3a-document-deleted.md §配置）。
public sealed class WolverineDocumentDeletedPublisher(IMessageBus bus) : IDocumentDeletedPublisher
{
    public async Task PublishDeletedAsync(
        Guid documentId, DateTimeOffset deletedAt, CancellationToken ct = default) =>
        // 🔴 イベントの構築をここに置くのは、`findPublishers` が `Publish(new <Event>(` にしか
        // 一致しないためである（組み立て済みの変数を渡すと発行が検査器から見えなくなる）。
        // ⚠️ Wolverine の PublishAsync は CancellationToken を取らない（発行のキャンセル伝播は
        // 失われる。E1 仕様書「受け入れた挙動差」で受容済みの API 差）。
        await bus.PublishAsync(new DocumentDeleted(documentId, deletedAt));
}
