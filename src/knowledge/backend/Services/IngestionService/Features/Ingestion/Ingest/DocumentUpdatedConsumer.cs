using Platform.Shared.Infrastructure.Foundation.Pipeline;
using IngestionService.Domain.Ports;
using IngestionService.Domain;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace IngestionService.Features.Ingestion.Ingest;

// FR-02, UC-04: DocumentUpdated を受信し、parse→chunk→embed→index のパイプラインで
// 文書をチャンク化し Qdrant（検索インデックス）へ登録する
//
// 🔴 ADR-0027 / E3b: **購読は Wolverine へ移した**（IPipelineStep<DocumentUpdated>・IADR-0239）。
// 発行（IngestionCompleted）は MassTransit のまま —— その辺は本 PR の射程外であり、
// 辺は原子的に動かす（IADR-0234 決定 3）。発行は IIngestionCompletedPublisher（ポート）越しで、
// 1 ファイル 1 トランスポートを保つ。
public class DocumentUpdatedConsumer(
    IDocumentContentReader reader,
    IChunkingService chunker,
    IEmbeddingService embed,
    IIngestionVectorStore store,
    IIngestionCompletedPublisher bus,
    ILogger<DocumentUpdatedConsumer> logger) : IPipelineStep<DocumentUpdated>
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "ingest";

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
        // **組織文書は常に true**（露出キーを持たない）なので、既存経路は 1 ビットも変わらない。
        //
        // 🔴 **`MarkdownUri` の判定より前に置く。** 撤収は本文の所在に依存しない ——
        // 後ろに置くと、本文を持たない資料のチャンクが消えずに残る。
        if (!DocumentExposure.IsIndexable(ev.Attributes))
        {
            await store.DeleteByDocumentFromAllAsync(ev.DocumentId, ct);
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
        await store.DeleteByDocumentFromAllAsync(ev.DocumentId, ct);

        // FR-05, ADR-0016: 文書の機密区分（ABAC confidentiality）を埋め込み越境判定へ渡す。
        var confidentiality = ev.Attributes.GetValueOrDefault("confidentiality");

        // FR-02 parse: 本文（Markdown）を取得する
        var markdownText = await reader.ReadAsync(ev.MarkdownUri, ev.Title, ct);

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
            await IndexMetadataOnlyAsync(ev, confidentiality, ct);
            return;
        }

        var chunkCount = 0;
        var skipped = 0;

        foreach (var (text, idx) in chunks.Select((t, i) => (t, i)))
        {
            // FR-02: documentId + chunkIndex から決定的なチャンク ID を導出（冪等）
            var chunkId = ChunkId.Derive(ev.DocumentId, idx);

            // FR-02 embed: 埋め込み生成（LLM Gateway 経由 / ADR-0013・ADR-0016）。機密区分で送信先・コレクションが決まる。
            var embedding = await embed.EmbedAsync(text, confidentiality, ct);

            // FR-02（Issue #98 レビュー対応）: 一時的な障害（送信先の不調・タイムアウト等）は fail-closed
            // （意図的拒否）と区別する。一時障害（Retryable=true）は恒久スキップにせず例外を送出し、
            // ブローカのリトライ/DLQ（Wolverine の UsePlatformMessagingDefaults）に委ねる
            // （一括再索引中に外部が一時不調でもチャンクを取りこぼさない）。
            if (!embedding.Embedded && embedding.Retryable)
            {
                logger.LogWarning(
                    "Ingestion {Id} chunk {Index}: transient embedding failure, retrying via broker (confidentiality={Confidentiality})",
                    ev.DocumentId, idx, confidentiality ?? "(unset)");
                throw new EmbeddingTransientException(ev.DocumentId, idx);
            }

            // FR-02, FR-05, ADR-0016: fail-closed。高機密でセルフホスト未有効・次元不整合など恒久的な理由は
            // 索引しない（外部へ本文を送らず、誤ったコレクション/次元へも書かない）。再試行では解消しないためスキップ。
            if (!embedding.Embedded)
            {
                skipped++;
                continue;
            }

            // FR-02 index: 機密区分ルーティングが決めたモデル別コレクションへ登録。chunk_index/tags、FR-05 ABAC 属性を保持
            // FR-03, SC-02, #536: 更新日時は**イベントが運んできた値をそのまま渡す**（IADR-0149 決定 5）。
            // ここで DateTimeOffset.UtcNow を採ると、再索引のたびに全文書の「更新日時」が今になる。
            // #1184: `shared_with` も同じ点へ載せる（ADR-0061 決定 5 の第 3 の判定軸）。
            await store.UpsertChunkAsync(embedding.Collection, chunkId, ev.DocumentId, ev.Title, text, idx,
                embedding.Vector, ev.MarkdownUri, ev.Attributes, ev.Tags, ev.UpdatedAt,
                ev.SharedWith, ct);
            chunkCount++;
        }

        if (skipped > 0)
        {
            // fail-closed のスキップ（高機密でセルフホスト未有効等）は監査可能にする。
            logger.LogWarning(
                "Ingestion {Id}: {Skipped} chunk(s) skipped (embedding fail-closed; confidentiality={Confidentiality})",
                ev.DocumentId, skipped, confidentiality ?? "(unset)");
        }

        // FR-02: 取り込み完了イベント発行 → 検索反映へ連鎖（発行は MassTransit のまま。ポート越し）
        await bus.PublishCompletedAsync(ev.DocumentId, chunkCount, DateTimeOffset.UtcNow, ct);

        logger.LogInformation("Ingestion complete for {Id}: {Count} chunks", ev.DocumentId, chunkCount);
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
        DocumentUpdated ev, string? confidentiality, CancellationToken ct)
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

        var embedding = await embed.EmbedAsync(indexText, confidentiality, ct);

        // 本文チャンクと同じ規則: 一時障害は例外（ブローカの再試行へ）、恒久的な拒否はスキップ。
        if (!embedding.Embedded && embedding.Retryable)
        {
            logger.LogWarning(
                "Ingestion {Id} metadata point: transient embedding failure, retrying via broker "
                + "(confidentiality={Confidentiality})", ev.DocumentId, confidentiality ?? "(unset)");
            throw new EmbeddingTransientException(ev.DocumentId, ChunkId.MetadataChunkIndex);
        }

        if (embedding.Embedded)
        {
            await store.UpsertMetadataPointAsync(embedding.Collection,
                ChunkId.DeriveMetadata(ev.DocumentId), ev.DocumentId, ev.Title, indexText,
                embedding.Vector, ev.MarkdownUri, ev.Attributes, ev.Tags, ev.UpdatedAt,
                ev.SharedWith, ct);

            logger.LogInformation(
                "Ingestion {Id}: no body; indexed metadata only (title/tags/path/source). 0 body chunks",
                ev.DocumentId);
        }
        else
        {
            logger.LogWarning(
                "Ingestion {Id}: metadata point skipped (embedding fail-closed; confidentiality={Confidentiality})",
                ev.DocumentId, confidentiality ?? "(unset)");
        }

        // FR-02: **本文なしでも取り込みは完了である**（ADR-0070 決定 3。失敗として溜めない）。
        // チャンク数は 0 —— 本文由来のチャンクは 1 件も作っていない。
        await bus.PublishCompletedAsync(ev.DocumentId, 0, DateTimeOffset.UtcNow, ct);
    }
}

// FR-02（Issue #98）: 埋め込みの一時的な障害を表す。送出するとブローカの受信リトライ／(枯渇後)DLQ に
// 回り、外部（Voyage 等）の一時不調でチャンクを恒久的に取りこぼすのを防ぐ。fail-closed（意図的拒否）とは区別する。
public sealed class EmbeddingTransientException(Guid documentId, int chunkIndex)
    : Exception($"Transient embedding failure for document {documentId} chunk {chunkIndex}");
