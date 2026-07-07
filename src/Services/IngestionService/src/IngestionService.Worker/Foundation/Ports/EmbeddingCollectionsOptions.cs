namespace IngestionService.Worker.Foundation.Ports;

// FR-02, ADR-0016: モデル別 Qdrant コレクションの定義（設定駆動）。
// 機密区分でモデル（次元）が分かれるため、コレクションをモデル別に分離する。取り込み時の索引先は
// ゲートウェイが機密区分から決めて返すが、起動時ブートストラップ（全コレクション作成）と
// 残存防止削除（全コレクション横断）のために全コレクションの一覧・次元を保持する。
public sealed class EmbeddingCollectionsOptions
{
    public const string SectionName = "Embedding:Collections";

    public List<EmbeddingCollectionOptions> Collections { get; set; } = [];
}

public sealed class EmbeddingCollectionOptions
{
    // モデル別コレクション名（例 knowledge_chunks_voyage_3_5 / knowledge_chunks_ruri_v3）。
    public string Name { get; set; } = string.Empty;

    // 当該コレクションのベクトル次元（voyage-3.5=1024 / ruri-v3=768 系）。
    public int VectorSize { get; set; }
}
