namespace IngestionService.Domain.Ports;

// FR-02, ADR-0009, ADR-0016: IngestionService が Qdrant へ書き込む際のポート。
// 機密区分でモデル（次元）が分かれるため、コレクションはモデル別に分離する。
public interface IIngestionVectorStore
{
    // FR-02: 全モデル別コレクション（索引）の存在を保証する（起動時ブートストラップ）。
    // ［2026-10-05 / #1746］[[IADR-0497]] 決定 1: 語彙索引（ベクトルを持たないコレクション）も作る。
    // `text` / `text_ngram` の索引と `text_ngram` の後付け（下の 2 つ）も語彙索引を含む全コレクションへ効く。
    Task EnsureCollectionsAsync(CancellationToken ct = default);

    // FR-03, #1118: 日本語（CJK）2-gram ペイロード `text_ngram` の全文索引を全コレクションへ張る
    // （新規・既存とも、存在の有無によらず冪等に）。起動時ブートストラップが `EnsureCollectionsAsync` の
    // 直後に呼ぶ。**別メソッドにしているのは、`text` の索引を固定している既存の試験を動かさないため**
    // （[[IADR-0339]] 決定 2）。
    //
    // 既定実装は何もしない。**索引を持たない試験用の実装（書き込みを記録するだけの偽物）に
    // 「維持する索引が無い」を表させるため**であり、実装（Qdrant）は必ず上書きする。
    // 上書き漏れは検索側の readiness（`qdrant-cjk-ngram-index` が Degraded）に現れる。
    Task EnsureCjkNgramIndexAsync(CancellationToken ct = default) => Task.CompletedTask;

    // FR-03, #1118: `text_ngram` を持たない既存の点へ、`text` から作った 2-gram を後付けする。
    // 埋めた点の数を返す（2 回目以降の起動では 0）。**再取り込みを要求しない**ための移行経路であり、
    // 起動後にバックグラウンドで走る（[[IADR-0339]] 決定 2）。既定実装（0 件）の位置づけは上と同じ。
    Task<int> BackfillCjkNgramAsync(CancellationToken ct = default) => Task.FromResult(0);

    // FR-04, FR-05, SC-01, SC-08, [[IADR-0502]] 決定 2 (i) (#1760): facet と ABAC フィルタが引く**集合値キー**
    // （`tags`・`shared_with`）のキーワード索引を全コレクションへ張る（新規・既存とも、存在の有無によらず冪等に）。
    // 起動時ブートストラップが `EnsureCjkNgramIndexAsync` の直後に呼ぶ。属性キー（`attributes.<key>`）は動的なので、
    // 実装は書き込みの口（`Upsert*`）でも張る。既定実装の位置づけは上の 2 つと同じ（索引を持たない偽物用）。
    Task EnsureKeywordIndexesAsync(CancellationToken ct = default) => Task.CompletedTask;

    // FR-04, FR-05, [[IADR-0502]] 決定 3 (#1760): 既存の点に現れる属性キーを拾い、キーワード索引を張る。
    // 張った数を返す。**再起動後に一度も書かれていないキー**にも索引を付けるための経路であり、
    // 起動後にバックグラウンドで走る（`QdrantKeywordIndexDiscoveryHostedService`）。既定実装（0 件）の位置づけは上と同じ。
    Task<int> EnsureKeywordIndexesForExistingPointsAsync(CancellationToken ct = default) => Task.FromResult(0);

    // FR-06, SC-05, [[IADR-0565]] 決定 2 (#1993): `document_id` のキーワード索引を全コレクション（モデル別 ＋ 語彙索引）へ張る
    // （冪等）。索引の状態の照会（`GetIndexPresenceAsync`）の facet が要る（索引の無いキーは facet できない）。
    // 起動時のブートストラップが `EnsureKeywordIndexesAsync` の直後に呼ぶ。既定実装の位置づけは上の 3 つと同じ。
    Task EnsureDocumentIdIndexAsync(CancellationToken ct = default) => Task.CompletedTask;

    // FR-06, SC-05, [[IADR-0565]] 決定 2 (#1993): 渡した文書のうち、どれが点を持つか（索引の状態の材料）。
    // 🔴 **問い合わせの回数は文書の数に依らない**（コレクションごとに facet を 1 回。語彙索引は印あり・なしの 2 回）。
    // 引けなければ例外（呼び出し元が「引けなかった」として扱う。空の集合＝未索引へ畳まない）。
    // 既定実装は投げる（索引を持たない偽物で「点が無い」が静かに通らないように）。
    Task<DocumentIndexPresence> GetIndexPresenceAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
        => throw new NotSupportedException("This vector store does not answer index presence");

    // FR-02: 指定コレクションへチャンクを索引する（コレクションはゲートウェイの機密区分ルーティングが決める）。
    // FR-03, SC-02, #536: `updatedAt` は文書の更新日時（DocumentUpdated.UpdatedAt）である。
    // **取り込み時刻を渡さないこと**（IADR-0149 決定 5）——渡すと再索引のたびに全文書の
    // 「更新日時」が今になり、計画が並び順を求めた動機そのものが成立しなくなる。
    // FR-19, FR-20, ADR-0061 決定 5 / [[IADR-0396]] 決定 3 (#1184): `sharedWith` は共有先の集合であり、
    // **属性辞書ではなく `tags` と同じリスト項目**として点に載る。null / 空は「誰とも共有していない」。
    Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        List<string>? sharedWith = null,
        CancellationToken ct = default);

    // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 1・2:
    // **本文を持たない文書を、メタデータだけで索引へ載せる（1 文書 1 点）。**
    //
    // `indexText` は題名・タグから作った**索引テキスト**であり（`MetadataIndexText`）、本文ではない。
    // 点には `has_body = false` が載り、検索側は復元時にこれを見て**本文抜粋を空にする**
    // （`DocumentBodyPresence.Excerpt`）。
    //
    // 🔴 **チャンクの口（`UpsertChunkAsync`）と分けてある。** 同じ口に真偽値を足すと、
    // 呼び出しの取り違えが**メタデータを本文として索引する**形で静かに通る。
    // 属性・タグ・更新日時はチャンクと同じ表現で載せる —— **ABAC の判定軸は本文の有無で変えない。**
    Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId, string title,
        string indexText, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        List<string>? sharedWith = null,
        CancellationToken ct = default);

    // FR-02, FR-03, FR-05, ADR-0127 決定 1・2, [[IADR-0497]] 決定 1・3 (#1746):
    // **高機密文書（confidential・restricted・未指定・未知）のチャンクを語彙索引へ書く。ベクトルは持たない。**
    //
    // 🔴 **コレクションもベクトルも引数に取らない。** 置き場所は語彙索引の 1 本に固定であり
    // （呼び出し側が選べると、ベクトルのコレクションへベクトル無しの点を書けてしまう）、
    // 意味の無いベクトル（ハッシュ等）で点を作る形は ADR-0127 決定 1 が退けている。
    // ペイロードはチャンクの口と同じ表現（`text` / `text_ngram` / `attributes` / `tags` / `shared_with` /
    // `updated_at` / `document_id`）—— ABAC・削除・並び順・全文索引の判定軸を、埋め込みの有無で変えない。
    Task UpsertLexicalChunkAsync(Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        List<string>? sharedWith = null,
        CancellationToken ct = default);

    // FR-02, FR-03, ADR-0070 決定 4, ADR-0127 決定 1, [[IADR-0497]] 決定 3 (#1746):
    // 本文を持たない高機密文書の**メタデータ点**を語彙索引へ書く（`has_body = false`。1 文書 1 点）。
    // メタデータの口（`UpsertMetadataPointAsync`）と同じく、チャンクの口と分けてある。
    Task UpsertLexicalMetadataPointAsync(Guid pointId, Guid documentId, string title,
        string indexText, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        List<string>? sharedWith = null,
        CancellationToken ct = default);

    // FR-02, FR-03, NFR-06, [[IADR-0536]] 決定 1・2 (#1908): **埋め込みが使えなかった public / internal のチャンクを、
    // 全文だけの点（埋め込み待ち）として語彙索引へ書く。** ペイロードは語彙索引のチャンクの口と同じ表現に
    // `embedding_pending = true` と理由（`EmbeddingPendingReasons`）を足したもの。鍵の投入後に埋め直しの器が拾う。
    //
    // 🔴 **高機密（`LexicalIndexPolicy.IsLexicalOnly`）の文書をここへ書かない。** 高機密は印の無い語彙索引の口
    // （`UpsertLexicalChunkAsync`）へ行き、埋め直しの対象にならない。
    //
    // 既定実装は**投げる**（何もしない既定にすると、索引を持たない偽物で「書かない」が静かに通る —— #1908 の欠陥の形そのもの）。
    Task UpsertEmbeddingPendingChunkAsync(Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt, List<string>? sharedWith, string reason,
        CancellationToken ct = default)
        => throw new NotSupportedException("This vector store does not hold embedding-pending points");

    // FR-02, ADR-0070 決定 4, [[IADR-0536]] 決定 1 (#1908): 本文なしの public / internal 文書のメタデータ点の埋め込み待ち版
    // （`has_body = false`）。既定実装の位置づけはチャンクの口と同じ。
    Task UpsertEmbeddingPendingMetadataPointAsync(Guid pointId, Guid documentId, string title,
        string indexText, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt, List<string>? sharedWith, string reason,
        CancellationToken ct = default)
        => throw new NotSupportedException("This vector store does not hold embedding-pending points");

    // FR-02, [[IADR-0536]] 決定 2 (#1908): 語彙索引の `embedding_pending` に真偽の索引を張る（冪等）。
    // 数える・拾う問い合わせを全走査にしないための最適化であり、無くても問い合わせは通る。
    // 埋め直しの器が最初の周期で呼ぶ。既定実装は何もしない（索引を持たない偽物用）。
    Task EnsureEmbeddingPendingIndexAsync(CancellationToken ct = default) => Task.CompletedTask;

    // FR-02, NFR-06, [[IADR-0536]] 決定 3 (#1908): 語彙索引の埋め込み待ちの点の件数（計器と Warning の材料）。
    // 既定実装は 0（索引を持たない偽物用。実装〔Qdrant〕は必ず上書きする）。
    Task<long> CountEmbeddingPendingAsync(CancellationToken ct = default) => Task.FromResult(0L);

    // FR-02, [[IADR-0536]] 決定 2 (#1908): 埋め込み待ちの点を最大 `limit` 件読む（ベクトルは読まない）。既定実装は空。
    // 印はあるのにペイロードから復元できない点（文書 ID が読めない等）は、落とさずに ID を `UnreadableIds` へ返す
    // （#1924 のレビュー。数える側〔`CountEmbeddingPendingAsync`〕は数え続けるので、黙って落とすとゲージが永久に 0 にならない）。
    Task<EmbeddingPendingPage> ListEmbeddingPendingAsync(int limit, CancellationToken ct = default)
        => Task.FromResult(EmbeddingPendingPage.Empty);

    // FR-02, [[IADR-0536]] 決定 2 (#1908): 1 点を読み直す（埋め直しの直前の再確認。無い・待ちでなければ null）。既定実装は null。
    Task<EmbeddingPendingPoint?> GetEmbeddingPendingAsync(Guid pointId, CancellationToken ct = default)
        => Task.FromResult<EmbeddingPendingPoint?>(null);

    // FR-02, [[IADR-0536]] 決定 2 (#1908): 埋め直した点を語彙索引から消す（**待ちの印を持つ点だけ**を消す条件つき）。
    // 既定実装は投げる（消し忘れを静かに通さない）。
    Task DeleteEmbeddingPendingAsync(Guid pointId, CancellationToken ct = default)
        => throw new NotSupportedException("This vector store does not hold embedding-pending points");

    // FR-02, FR-05: 全モデル別コレクションから当該文書のチャンクを削除する。
    // ［2026-10-05 / #1746］[[IADR-0497]] 決定 4: **語彙索引のコレクションからも消す**（機密区分が
    // confidential → public へ下がったとき、語彙索引の古い点が残らないように）。
    // 機密区分変更（例 public→confidential）でモデル/コレクションが変わっても旧コレクションに残存させない
    // （残存すると ABAC を跨いだ検索ヒットになり得るため fail-closed で全消しする）。
    //
    // #1193: **メタデータ点（本文なし）も同じ `document_id` を持つ**ので、この 1 本で一緒に消える。
    // 本文が生えた／消えた文書は、次の取り込みでチャンクとメタデータ点が入れ替わる（両方は残らない）。
    Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default);

    // FR-02, FR-05, [[IADR-0595]] 決定 1 (#2101): 文書の**版の印**（索引へ最後に反映した版）を読む。無ければ null。
    // 🔴 版の印は検索のコレクションとは別の場所に置く（検索側は読まない）。点を全部消した文書（撤収・削除）でも残る ——
    // 消した後に古い更新が届いても、印より古ければ反映しないためである。
    // 既定実装は投げる（版を持たない偽物で「古い更新を弾く」が静かに素通りしないように）。
    Task<DocumentIndexVersion?> GetIndexVersionAsync(Guid documentId, CancellationToken ct = default)
        => throw new NotSupportedException("This vector store does not hold document index versions");

    // FR-02, FR-05, [[IADR-0595]] 決定 1 (#2101): 版の印を書く（文書 ID ・版・判定軸の指紋だけ。題名・本文・属性の値は書かない）。
    Task SetIndexVersionAsync(Guid documentId, DocumentIndexVersion version, CancellationToken ct = default)
        => throw new NotSupportedException("This vector store does not hold document index versions");

    // FR-02, FR-05, [[IADR-0595]] 決定 2 (#2101): **旧版の点を消す。** 全コレクション（モデル別 ＋ 語彙索引）から、
    // 文書の点のうち `updated_at` が `version` 以下（または `updated_at` を持たない）で、かつ `keep` に無い点を消す。
    // `version` より**厳密に新しい**点は残す（並行して反映した新しい版を古い版の後始末で消さない）。
    // 既定実装は投げる（後始末の漏れを静かに通さない）。
    Task DeleteSupersededAsync(Guid documentId, DateTimeOffset version, IReadOnlyCollection<IndexedPointRef> keep,
        CancellationToken ct = default)
        => throw new NotSupportedException("This vector store does not delete superseded points");
}
