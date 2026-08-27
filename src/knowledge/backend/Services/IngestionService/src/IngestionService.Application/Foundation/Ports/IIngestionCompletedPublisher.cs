namespace IngestionService.Application.Foundation.Ports;

// FR-02 / ADR-0027（E3b）: 取り込み完了イベントの発行口。
//
// 🔴 **抽象を挟む理由は設計の好みではない。トポロジ検査の導出単位がファイルだからである。**
// DocumentUpdatedConsumer の購読が Wolverine へ移った（E3b）が、IngestionCompleted の発行が
// 属する辺は本 PR の射程外で MassTransit のまま残る。同一ファイルに両トランスポートの
// `using` が同居すると、発行が `masstransit+wolverine` の union として記録され、
// IngestionCompleted の辺を移すときに違反が報告されなくなる（E1 の
// IDocumentNormalizedPublisher と同じ理由。IADR-0245）。
//
// ⚠️ イベントの構築は実装側（アダプタ）に置く（`findPublishers` の可視性を保つため）。
public interface IIngestionCompletedPublisher
{
    Task PublishCompletedAsync(
        Guid documentId, int chunkCount, DateTimeOffset completedAt, CancellationToken ct = default);
}
