namespace IngestionService.Domain.Ports;

// FR-02, ADR-0016: モデル別 Qdrant コレクションの定義（設定駆動）。
// ［2026-10-17 / #1746 段 S4］既定は voyage の 1 本だけである。セルフホスト（Ruri）のコレクションは
// opt-in（Helm `embedding.enabled`・compose の `SEARCH_FUSED_COLLECTION`）のときだけ足される（ADR-0127 決定 5）。
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

    // FR-02, ADR-0127 決定 5, [[IADR-0085]] (#1746 段 S4): **名前が空の定義を捨てる。**
    // セルフホスト（Ruri）のコレクションは既定に無く、opt-in を有効にしたときだけ添字 1 に足される。
    // compose は検索と同じ `${SEARCH_FUSED_COLLECTION:-}` を与えるので、無効のときは空文字で届く
    // （検索側の `ResolveFusedCollectionNames` が空を捨てるのと同じ作法）。捨てないと名前の無い
    // コレクションを作ろうとして起動時のブートストラップが失敗する。
    public EmbeddingCollectionsOptions DropUnnamed()
    {
        Collections.RemoveAll(c => string.IsNullOrWhiteSpace(c.Name));
        return this;
    }
}

public sealed class EmbeddingCollectionOptions
{
    // モデル別コレクション名（例 knowledge_chunks_voyage_3_5。opt-in のときだけ knowledge_chunks_ruri_v3）。
    public string Name { get; set; } = string.Empty;

    // 当該コレクションのベクトル次元（voyage-3.5=1024 / ruri-v3=768 系）。
    public int VectorSize { get; set; }
}
