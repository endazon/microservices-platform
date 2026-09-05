namespace IngestionService.Domain.Ports;

// FR-02, ADR-0009, ADR-0016: IngestionService が Qdrant へ書き込む際のポート。
// 機密区分でモデル（次元）が分かれるため、コレクションはモデル別に分離する。
public interface IIngestionVectorStore
{
    // FR-02: 全モデル別コレクション（索引）の存在を保証する（起動時ブートストラップ）。
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

    // FR-02, FR-05: 全モデル別コレクションから当該文書のチャンクを削除する。
    // 機密区分変更（例 public→confidential）でモデル/コレクションが変わっても旧コレクションに残存させない
    // （残存すると ABAC を跨いだ検索ヒットになり得るため fail-closed で全消しする）。
    //
    // #1193: **メタデータ点（本文なし）も同じ `document_id` を持つ**ので、この 1 本で一緒に消える。
    // 本文が生えた／消えた文書は、次の取り込みでチャンクとメタデータ点が入れ替わる（両方は残らない）。
    Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default);
}
