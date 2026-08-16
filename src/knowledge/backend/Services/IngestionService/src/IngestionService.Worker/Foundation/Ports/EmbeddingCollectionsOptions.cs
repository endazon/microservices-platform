namespace IngestionService.Worker.Foundation.Ports;

// FR-02, ADR-0016: モデル別 Qdrant コレクションの定義（設定駆動）。
// 機密区分でモデル（次元）が分かれるため、コレクションをモデル別に分離する。取り込み時の索引先は
// ゲートウェイが機密区分から決めて返すが、起動時ブートストラップ（全コレクション作成）と
// 残存防止削除（全コレクション横断）のために全コレクションの一覧・次元を保持する。
public sealed class EmbeddingCollectionsOptions
{
    // #806: セクションは `Embedding` であって `Embedding:Collections` ではない。
    // 後者にすると、バインダは配列そのものから更に `Collections` プロパティを探して
    // `Embedding:Collections:Collections` を見にいき、**存在しないので空リストのままバインドが成功する**
    // （例外は出ない）。結果、EnsureCollectionsAsync の foreach が 0 回まわってコレクションが作られず、
    // DeleteByDocumentFromAllAsync も無言の no-op になる（機密区分の引き上げ時に旧コレクションへ残る）。
    // 隣の EmbeddingRoutingOptions が `Embedding:Routing` ＋ プロパティ `Endpoints` で重複していないのと同じ形に揃える。
    public const string SectionName = "Embedding";

    public List<EmbeddingCollectionOptions> Collections { get; set; } = [];
}

public sealed class EmbeddingCollectionOptions
{
    // モデル別コレクション名（例 knowledge_chunks_voyage_3_5 / knowledge_chunks_ruri_v3）。
    public string Name { get; set; } = string.Empty;

    // 当該コレクションのベクトル次元（voyage-3.5=1024 / ruri-v3=768 系）。
    public int VectorSize { get; set; }
}
