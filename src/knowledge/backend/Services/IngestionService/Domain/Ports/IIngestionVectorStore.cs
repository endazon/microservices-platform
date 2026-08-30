namespace IngestionService.Domain.Ports;

// FR-02, ADR-0009, ADR-0016: IngestionService が Qdrant へ書き込む際のポート。
// 機密区分でモデル（次元）が分かれるため、コレクションはモデル別に分離する。
public interface IIngestionVectorStore
{
    // FR-02: 全モデル別コレクション（索引）の存在を保証する（起動時ブートストラップ）。
    Task EnsureCollectionsAsync(CancellationToken ct = default);

    // FR-02: 指定コレクションへチャンクを索引する（コレクションはゲートウェイの機密区分ルーティングが決める）。
    // FR-03, SC-02, #536: `updatedAt` は文書の更新日時（DocumentUpdated.UpdatedAt）である。
    // **取り込み時刻を渡さないこと**（IADR-0149 決定 5）——渡すと再索引のたびに全文書の
    // 「更新日時」が今になり、計画が並び順を求めた動機そのものが成立しなくなる。
    Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        CancellationToken ct = default);

    // FR-02, FR-05: 全モデル別コレクションから当該文書のチャンクを削除する。
    // 機密区分変更（例 public→confidential）でモデル/コレクションが変わっても旧コレクションに残存させない
    // （残存すると ABAC を跨いだ検索ヒットになり得るため fail-closed で全消しする）。
    Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default);
}
