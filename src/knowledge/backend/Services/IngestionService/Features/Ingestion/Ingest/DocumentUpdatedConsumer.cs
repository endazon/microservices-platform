using Platform.Shared.Infrastructure.Foundation.Pipeline;
using IngestionService.Domain.Ports;
using IngestionService.Domain;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using IngestionService.Common.Observability;

namespace IngestionService.Features.Ingestion.Ingest;

// FR-02, UC-04: DocumentUpdated を受信し、parse→chunk→embed→index のパイプラインで
// 文書をチャンク化し Qdrant（検索インデックス）へ登録する
//
// 🔴 ADR-0027 / E3b: **購読は Wolverine へ移した**（IPipelineStep<DocumentUpdated>・IADR-0239）。
// 発行（IngestionCompleted）は MassTransit のまま —— その辺は本 PR の射程外であり、
// 辺は原子的に動かす（IADR-0234 決定 3）。発行は IIngestionCompletedPublisher（ポート）越しで、
// 1 ファイル 1 トランスポートを保つ。
//
// 🔴 #1640: **外への呼び出し（本文の取得・埋め込み・Qdrant）はすべて呼び出しごとの期限の下で行う**（`IngestionTimeouts`・
// `ConsumerCallTimeouts`）。受け口の ct は Wolverine の実行期限を含むため、期限が無いと止まった依存先は
// 受け口の ct で「取り消し」として切られる。自分の期限の時間切れは `ConsumerTimeoutException` として投げ
// （警告ログ・計器 `messaging.consumer.timeout`）、呼び出し元の取り消しはそのまま外へ出す。
// 埋め込みはチャンクごとに総枠（`EmbeddingBudget`）を判定し、使い切ったら残りを呼ばずに時間切れとして投げる。
// ［#1771］この辺の移行は IADR-0234 の単位 E4 として追跡する。購読者は結線しない（planning#741 項目 6 の裁定・2026-10-09）。
public class DocumentUpdatedConsumer(
    IDocumentContentReader reader,
    IChunkingService chunker,
    IEmbeddingService embed,
    IIngestionVectorStore store,
    IIngestionCompletedPublisher bus,
    ConsumerCallTimeouts calls,
    IngestionTimeouts timeouts,
    TimeProvider clock,
    EmbeddingPendingMetrics pendingMetrics,
    ILogger<DocumentUpdatedConsumer> logger) : IPipelineStep<DocumentUpdated>
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "ingest";

    // #1640: 外への 1 回の呼び出しを、その呼び出し先の期限の下で行う。
    private Task<T> CallAsync<T>(string target, TimeSpan timeout,
        Func<CancellationToken, Task<T>> call, CancellationToken ct)
        => calls.RunAsync(StepName, target, timeout, call, ct);

    private Task CallAsync(string target, TimeSpan timeout,
        Func<CancellationToken, Task> call, CancellationToken ct)
        => calls.RunAsync(StepName, target, timeout, call, ct);

    private Task DeleteFromAllAsync(Guid documentId, CancellationToken ct)
        => CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.DeleteFromAll,
            t => store.DeleteByDocumentFromAllAsync(documentId, t), ct);

    private Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct)
        => CallAsync(IngestionTimeouts.EmbeddingTarget, timeouts.Embedding,
            t => embed.EmbedAsync(text, confidentiality, t), ct);

    // ADR-0027 / E3b: Wolverine のハンドラ。
    public async Task Handle(DocumentUpdated ev, CancellationToken ct)
    {
        // 🔴 FR-19, ADR-0061 決定 1・2・4 / [[IADR-0396]] 決定 4・5 (#1184): **索引の門。**
        //
        // 露出 3 トグルのうち 1 つでも ON なら載せる。**3 つとも OFF なら載せないだけでなく、
        // 既に載っているチャンクを削除する**（決定 4。ON → OFF は索引からの削除まで及ぶ）。
        //
        // **「属性で弾く」で済ませない。** 索引に本文を残したまま消費側のフィルタで隠す形は、
        // フィルタの実装ミス 1 つで露出に変わる（`ADR-0057` 決定 1・SC-19 の
        // 「いかなる方法でも復元できません」と同じ理由）。**実体を消す。**
        //
        // 判定は `DocumentExposure.IsIndexable` —— **発行側（DocumentService の門）と同じ関数**である。
        // 生産側と消費側で述語を割ると、片方だけ改名されて静かに無効化される。
        // **露出キーを持たない組織文書は true** なので、既存経路は 1 ビットも変わらない。3 つとも `excluded` を
        // 明示した組織文書（#1879 / [[IADR-0529]]）は個人資料と同じくここで撤収される。
        //
        // 🔴 **`MarkdownUri` の判定より前に置く。** 撤収は本文の所在に依存しない ——
        // 後ろに置くと、本文を持たない資料のチャンクが消えずに残る。
        if (!DocumentExposure.IsIndexable(ev.Attributes))
        {
            await DeleteFromAllAsync(ev.DocumentId, ct);
            logger.LogInformation(
                "DocumentUpdated {Id}: exposure toggles are all off; withdrew the document from every index",
                ev.DocumentId);
            return;
        }

        // FR-02 例外フロー E1: 本文の所在が無ければ取り込みをスキップ
        if (ev.MarkdownUri is null)
        {
            logger.LogWarning("DocumentUpdated {Id}: MarkdownUri is null, skipping ingestion", ev.DocumentId);
            return;
        }

        logger.LogInformation("Ingesting document {Id} title={Title}", ev.DocumentId, ev.Title);

        // FR-02, FR-05, ADR-0016: 既存チャンクを全モデル別コレクションから削除する。
        // 再インデックスの冪等性に加え、機密区分変更（例 public→confidential）でモデル/コレクションが
        // 変わった場合の旧コレクション残存（ABAC バイパス）を防ぐ（fail-closed で全消し）。
        await DeleteFromAllAsync(ev.DocumentId, ct);

        // FR-05, ADR-0016: 文書の機密区分（ABAC confidentiality）を埋め込み越境判定へ渡す。
        var confidentiality = ev.Attributes.GetValueOrDefault("confidentiality");

        // 🔴 FR-02, FR-05, ADR-0127 決定 1・4, [[IADR-0497]] 決定 2 (#1746): **高機密文書は埋め込まない。**
        // confidential・restricted・未指定・未知は、埋め込みを**呼ぶ前に**分けて語彙索引へだけ書く。
        // 本文はどの埋め込みの送信先（ティア A・B を問わず）へも渡らない。判定は allow-list
        // （public / internal だけが埋め込みへ進む）。ゲートウェイの越境判定（fail-closed）は残る —— 二重の守りである。
        var lexicalOnly = LexicalIndexPolicy.IsLexicalOnly(ev.Attributes);

        // FR-02 parse: 本文（Markdown）を取得する
        string markdownUri = ev.MarkdownUri;
        var markdownText = await CallAsync(IngestionTimeouts.ContentTarget, timeouts.ContentRead,
            t => reader.ReadAsync(markdownUri, ev.Title, t), ct);

        // FR-02 chunk: チャンク化
        var chunks = chunker.Chunk(markdownText);

        // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 1:
        // 🔴 **チャンクが 0 件になったときが「本文なし」である。** 本文（の分割結果）そのもので判定し、
        // **上流の状態名（変換側の「本文なしで完了」）には依存しない** ——
        // 依存すると、状態名の改名や別経路（直接投入・再正規化）で静かに漏れる。
        //
        // 本文由来のチャンク・埋め込みは作らない（作れない）が、**メタデータで索引へ載せる。**
        // 載せなければ利用者はその文書の存在を知る手段を持たない（ADR-0070 決定 4 / P-01）。
        WarnOnBodyPresenceDivergence(ev, chunks.Count);

        if (chunks.Count == 0)
        {
            await IndexMetadataOnlyAsync(ev, confidentiality, lexicalOnly, ct);
            return;
        }

        if (lexicalOnly)
        {
            await IndexLexicallyAsync(ev, chunks, markdownUri, confidentiality, ct);
            return;
        }

        var chunkCount = 0;
        // FR-02, NFR-06, [[IADR-0536]] 決定 1 (#1908): 埋め込みが使えなかったチャンクの位置と理由（使えていれば -1）。
        var pendingFrom = -1;
        var pendingReason = EmbeddingPendingReasons.Transient;
        // #1640: 埋め込みの総枠の起点（チャンクを回し始めた時刻）。
        var embeddingStarted = clock.GetTimestamp();

        foreach (var (text, idx) in chunks.Select((t, i) => (t, i)))
        {
            // FR-02: documentId + chunkIndex から決定的なチャンク ID を導出（冪等）
            var chunkId = ChunkId.Derive(ev.DocumentId, idx);

            // 🔴 #1640: 総枠は呼び出しの**前**に判定する（超過は最後の 1 チャンクの期限までに収まる）。
            // 使い切ったら残りを呼ばずに時間切れとして投げる —— 受け口の実行期限に切られて取り消しとして落ちるより先に。
            if (clock.GetElapsedTime(embeddingStarted) >= timeouts.EmbeddingBudget)
                throw calls.BudgetExhausted(StepName, IngestionTimeouts.EmbeddingBudgetTarget,
                    timeouts.EmbeddingBudget,
                    $"document {ev.DocumentId}: {idx} of {chunks.Count} chunks embedded");

            // FR-02 embed: 埋め込み生成（LLM Gateway 経由 / ADR-0013・ADR-0016）。機密区分で送信先・コレクションが決まる。
            var embedding = await EmbedAsync(text, confidentiality, ct);

            // 🔴 FR-02, FR-03, NFR-06, [[IADR-0536]] 決定 1 (#1908): **埋め込めなかったら、このチャンクから後ろを
            // 全文だけの点（埋め込み待ち）として語彙索引へ書く。** 一時障害（`Retryable=true`。鍵の未設定・送信先の不調）も
            // 恒久的な拒否（`Retryable=false`。経路の無効化・次元不整合）も同じに扱い、例外を投げない（DLQ へ送らない）。
            // 鍵の投入後に埋め直しの器（`EmbeddingPendingReembedder`）が主へ移し、語彙索引の点を消す。
            //
            // ［2026-10-14］従前は一時障害を例外で再試行の後 DLQ へ送り（#98）、恒久的な拒否をスキップして（[[IADR-0497]] 決定 2
            // 「埋め込めるはずの文書の不調を語彙索引で覆い隠さない」）、どちらも**点を書かなかった**。そのため鍵の無い間、
            // public / internal の文書は全文検索にも現れなかった（PoC で AST の情報収集が 0 件）。オーナー裁定（2026-10-14）で改めた。
            // 覆い隠さないために、待ちの件数を計器（`EmbeddingPendingMetrics`）と周期ごとの Warning で読めるようにしてある。
            //
            // 残りのチャンクは**埋め込みを呼ばない**（使えない送信先をチャンクの数だけ叩かない。総枠も消費しない）。
            if (!embedding.Embedded)
            {
                pendingFrom = idx;
                pendingReason = EmbeddingPendingReasons.From(embedding);
                break;
            }

            // FR-02 index: 機密区分ルーティングが決めたモデル別コレクションへ登録。chunk_index/tags、FR-05 ABAC 属性を保持
            // FR-03, SC-02, #536: 更新日時は**イベントが運んできた値をそのまま渡す**（IADR-0149 決定 5）。
            // ここで DateTimeOffset.UtcNow を採ると、再索引のたびに全文書の「更新日時」が今になる。
            // #1184: `shared_with` も同じ点へ載せる（ADR-0061 決定 5 の第 3 の判定軸）。
            await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                t => store.UpsertChunkAsync(embedding.Collection, chunkId, ev.DocumentId, ev.Title, text, idx,
                    embedding.Vector, markdownUri, ev.Attributes, ev.Tags, ev.UpdatedAt,
                    ev.SharedWith, t), ct);
            chunkCount++;
        }

        var pendingCount = 0;
        if (pendingFrom >= 0)
        {
            for (var idx = pendingFrom; idx < chunks.Count; idx++)
            {
                var chunkId = ChunkId.Derive(ev.DocumentId, idx);
                var text = chunks[idx];
                var i = idx;
                await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                    t => store.UpsertEmbeddingPendingChunkAsync(chunkId, ev.DocumentId, ev.Title, text, i,
                        markdownUri, ev.Attributes, ev.Tags, ev.UpdatedAt, ev.SharedWith, pendingReason, t), ct);
                pendingCount++;
            }

            pendingMetrics.RecordWritten(pendingCount, pendingReason);
            logger.LogWarning(
                "Ingestion {Id}: embedding unavailable ({Reason}; confidentiality={Confidentiality}); {Pending} of {Total} "
                + "chunk(s) written to the lexical index as full-text-only points awaiting embedding (keyword/hybrid search "
                + "finds them, semantic search does not). They are re-embedded once embedding works again (IADR-0536)",
                ev.DocumentId, pendingReason, confidentiality ?? "(unset)", pendingCount, chunks.Count);
        }

        // FR-02: 取り込み完了イベントを発行する（発行は MassTransit のまま。ポート越し）。
        // 購読者は無く、結線もしない（#1771 / planning#741 項目 6）。検索への反映は上の Qdrant 登録で成立している。
        // ［2026-10-14 / #1908］チャンク数は書いた点の数（埋めた数 ＋ 埋め込み待ちの数）である。
        await bus.PublishCompletedAsync(ev.DocumentId, chunkCount + pendingCount, DateTimeOffset.UtcNow, ct);

        logger.LogInformation("Ingestion complete for {Id}: {Count} chunks ({Pending} awaiting embedding)",
            ev.DocumentId, chunkCount + pendingCount, pendingCount);
    }

    // FR-02, FR-03, ADR-0127 決定 1・2, [[IADR-0497]] 決定 2・3 (#1746): 高機密文書の本文チャンクを語彙索引へ書く。
    // 🔴 **埋め込みを 1 回も呼ばない**ので、埋め込みの総枠（`EmbeddingBudget`）も一時障害の分岐も通らない。
    // Qdrant への書き込みは本文チャンクと同じ期限（`VectorStore`）の下で行い、失敗は例外のまま上げて
    // ブローカの再試行へ委ねる（本文チャンクの書き込みと同じ）。
    // チャンク ID は本文チャンクと同じ規則（`ChunkId.Derive`）—— 機密区分が変わって置き場所が移っても、
    // 冒頭の全コレクション削除で古い点は消えている。
    private async Task IndexLexicallyAsync(DocumentUpdated ev, IReadOnlyList<string> chunks,
        string markdownUri, string? confidentiality, CancellationToken ct)
    {
        var chunkCount = 0;
        foreach (var (text, idx) in chunks.Select((t, i) => (t, i)))
        {
            var chunkId = ChunkId.Derive(ev.DocumentId, idx);
            await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                t => store.UpsertLexicalChunkAsync(chunkId, ev.DocumentId, ev.Title, text, idx,
                    markdownUri, ev.Attributes, ev.Tags, ev.UpdatedAt, ev.SharedWith, t), ct);
            chunkCount++;
        }

        logger.LogInformation(
            "Ingestion {Id}: {Count} chunk(s) indexed into the lexical index without embedding "
            + "(confidentiality={Confidentiality}; ADR-0127)", ev.DocumentId, chunkCount,
            confidentiality ?? "(unset)");

        await bus.PublishCompletedAsync(ev.DocumentId, chunkCount, DateTimeOffset.UtcNow, ct);
    }

    // FR-02, FR-12, ADR-0070 決定 3, #1254, [[IADR-0388]] 決定 3:
    // **契約が運ぶ「本文の有無」と、ここでの判定（チャンク 0 件）が食い違ったら警告を残す。**
    //
    // 🔴 **判定そのものは変えない。** 索引は今までどおりチャンク 0 件で決める
    // （[[IADR-0358]] 決定 1。上流の状態名に依存すると、改名や別経路で静かに漏れる）。
    // ここが足すのは**観測だけ**である —— 二重化した情報のどちらかだけが変わったとき、
    // 従来は誰も気づかないまま索引の中身が割れていた。
    //
    // ⚠️ **`HasBody=true` で 0 件は「変換以外の経路で空本文が投入された」でも起きる**
    // （直接投入・空ファイル）。これは異常ではないが、**黙って本文なし扱いにするのは異常**なので
    // 同じ口で鳴らす。既定値（項目を運ばない旧発行元）も `true` なのでここに入り得る。
    private void WarnOnBodyPresenceDivergence(DocumentUpdated ev, int chunkCount)
    {
        if (ev.HasBody && chunkCount == 0)
        {
            logger.LogWarning(
                "Ingestion {Id}: contract says the document has a body (hasBody=true) but chunking "
                + "produced 0 chunks; indexing metadata only. Either the body is empty/unreadable or "
                + "the upstream marker is stale",
                ev.DocumentId);
        }
        else if (!ev.HasBody && chunkCount > 0)
        {
            logger.LogWarning(
                "Ingestion {Id}: contract says the document has no body (hasBody=false) but chunking "
                + "produced {Count} chunk(s); indexing them. The upstream marker and the body disagree",
                ev.DocumentId, chunkCount);
        }
    }

    // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 1・2・7:
    // 本文なしの文書を**メタデータ点 1 つ**で索引する。
    //
    // ベクトルは**索引テキスト（題名・タグ）から**作る —— 本文由来ではないので
    // 「本文由来の埋め込みは作らない」（決定 4）に反しない。
    // 🔴 **埋め込みの機密区分ルーティング（ADR-0016）は本文チャンクと同一に扱う。**
    // 本文が無いことを理由に送信制御を緩めない —— 題名も文書の内容である。
    private async Task IndexMetadataOnlyAsync(
        DocumentUpdated ev, string? confidentiality, bool lexicalOnly, CancellationToken ct)
    {
        // #1253 / [[IADR-0388]] 決定 4: 題名・タグに加えて**原本の所在とデータソース名**も材料にする
        // （ADR-0070 決定 4 が名指しする「パス」「データソース」。従前は届いていなかった）。
        var indexText = MetadataIndexText.Build(ev.Title, ev.Tags, ev.OriginalPath, ev.DataSourceName);

        // 索引テキストが空（題名もタグも無い）なら載せる意味が無い。**当たりようがない点を作らない。**
        if (indexText.Length == 0)
        {
            logger.LogWarning(
                "Ingestion {Id}: no body and no metadata to index (empty title and tags); nothing indexed",
                ev.DocumentId);
            await bus.PublishCompletedAsync(ev.DocumentId, 0, DateTimeOffset.UtcNow, ct);
            return;
        }

        // 🔴 FR-02, ADR-0070 決定 4, ADR-0127 決定 1, [[IADR-0497]] 決定 3 (#1746): 高機密文書のメタデータ点も
        // **埋め込まずに語彙索引へ書く**（題名も文書の内容である —— 本文チャンクと同じ規則）。
        if (lexicalOnly)
        {
            await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                t => store.UpsertLexicalMetadataPointAsync(ChunkId.DeriveMetadata(ev.DocumentId),
                    ev.DocumentId, ev.Title, indexText, ev.MarkdownUri, ev.Attributes, ev.Tags,
                    ev.UpdatedAt, ev.SharedWith, t), ct);
            logger.LogInformation(
                "Ingestion {Id}: no body; indexed metadata only into the lexical index without embedding "
                + "(confidentiality={Confidentiality})", ev.DocumentId, confidentiality ?? "(unset)");
            await bus.PublishCompletedAsync(ev.DocumentId, 0, DateTimeOffset.UtcNow, ct);
            return;
        }

        var embedding = await EmbedAsync(indexText, confidentiality, ct);

        if (embedding.Embedded)
        {
            await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                t => store.UpsertMetadataPointAsync(embedding.Collection,
                    ChunkId.DeriveMetadata(ev.DocumentId), ev.DocumentId, ev.Title, indexText,
                    embedding.Vector, ev.MarkdownUri, ev.Attributes, ev.Tags, ev.UpdatedAt,
                    ev.SharedWith, t), ct);

            logger.LogInformation(
                "Ingestion {Id}: no body; indexed metadata only (title/tags/path/source). 0 body chunks",
                ev.DocumentId);
        }
        else
        {
            // 🔴 FR-02, NFR-06, [[IADR-0536]] 決定 1 (#1908): 本文チャンクと同じ規則 —— 埋め込めなければ（一時障害・恒久的な
            // 拒否とも）**メタデータ点を全文だけの点（埋め込み待ち）として語彙索引へ書く**。例外は投げない。
            var reason = EmbeddingPendingReasons.From(embedding);
            await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                t => store.UpsertEmbeddingPendingMetadataPointAsync(ChunkId.DeriveMetadata(ev.DocumentId),
                    ev.DocumentId, ev.Title, indexText, ev.MarkdownUri, ev.Attributes, ev.Tags,
                    ev.UpdatedAt, ev.SharedWith, reason, t), ct);
            pendingMetrics.RecordWritten(1, reason);
            logger.LogWarning(
                "Ingestion {Id}: no body; embedding unavailable ({Reason}; confidentiality={Confidentiality}); metadata "
                + "point written to the lexical index as a full-text-only point awaiting embedding (IADR-0536)",
                ev.DocumentId, reason, confidentiality ?? "(unset)");
        }

        // FR-02: **本文なしでも取り込みは完了である**（ADR-0070 決定 3。失敗として溜めない）。
        // チャンク数は 0 —— 本文由来のチャンクは 1 件も作っていない。
        await bus.PublishCompletedAsync(ev.DocumentId, 0, DateTimeOffset.UtcNow, ct);
    }
}
