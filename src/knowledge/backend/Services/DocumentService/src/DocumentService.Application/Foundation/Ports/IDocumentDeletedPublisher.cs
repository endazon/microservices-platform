namespace DocumentService.Application.Foundation.Ports;

// FR-06 / ADR-0027（E3a）: 文書削除イベントの発行口。
//
// 🔴 **この抽象を挟む理由は設計の好みではない。トポロジ検査の導出単位がファイルだからである。**
// `scripts/check-event-topology.js` の `transportsOfFile` は、ファイル中の `using` から
// そのファイルのトランスポートを導出する。DocumentEndpoints.cs には DocumentUpdated の
// MassTransit 発行（辺 E3b の射程）が残るため、同ファイルで DocumentDeleted を Wolverine 発行
// すると `using` が同居し、発行が `masstransit+wolverine` の union として記録される
// （E1 の IDocumentNormalizedPublisher と同じ理由。IADR-0245 副産物の非対称性）。
//
// よって発行を別ファイル（アダプタ）へ切り出し、1 ファイルに 1 トランスポートだけが現れるようにする。
//
// ⚠️ **イベントの構築は実装側（アダプタ）に置く。** ここで契約型を組み立てて `Publish(ev)` の
// 形にすると、`findPublishers` の regex（`Publish(new <Event>(` にしか一致しない）から
// 発行が見えなくなり、不可視発行元を新たに作ってしまう。引数は素の値で渡す。
public interface IDocumentDeletedPublisher
{
    // ⚠️ Wolverine の `IMessageBus.PublishAsync` は CancellationToken を取らない（IADR-0245 /
    // E1 仕様書「受け入れた挙動差」）。ct は契約として受けるが、現行実装では伝播されない。
    Task PublishDeletedAsync(Guid documentId, DateTimeOffset deletedAt, CancellationToken ct = default);
}
