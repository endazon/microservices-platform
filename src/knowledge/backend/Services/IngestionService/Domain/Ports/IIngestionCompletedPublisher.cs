namespace IngestionService.Domain.Ports;

// FR-02 / ADR-0027（E3b）: 取り込み完了イベントの発行口。
//
// 抽象を挟むのは、トポロジ検査の導出単位がファイルだからである（受け口と発行口でトランスポートの `using` を分ける。IADR-0245）。
// ［2026-10-25 / #2134］辺 IngestionCompleted は Wolverine へ移った（IADR-0234 単位 E4 の完了。実装は `WolverineIngestionCompletedPublisher`）。
//
// ⚠️ イベントの構築は実装側（アダプタ）に置く（`findPublishers` の可視性を保つため）。
// ［#1771］購読者は結線しない（planning#741 項目 6 の裁定・2026-10-09）。
public interface IIngestionCompletedPublisher
{
    Task PublishCompletedAsync(
        Guid documentId, int chunkCount, DateTimeOffset completedAt, CancellationToken ct = default);
}
