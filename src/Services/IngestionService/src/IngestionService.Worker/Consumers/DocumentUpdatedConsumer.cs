using IngestionService.Worker.Services;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace IngestionService.Worker.Consumers;

// FR-02, UC-04: DocumentUpdated を受信し、parse→chunk→embed→index のパイプラインで
// 文書をチャンク化し Qdrant（検索インデックス）へ登録する
public class DocumentUpdatedConsumer(
    IDocumentContentReader reader,
    IChunkingService chunker,
    IEmbeddingService embed,
    IIngestionVectorStore store,
    IPublishEndpoint bus,
    ILogger<DocumentUpdatedConsumer> logger) : IConsumer<DocumentUpdated>
{
    public async Task Consume(ConsumeContext<DocumentUpdated> context)
    {
        var ev = context.Message;
        var ct = context.CancellationToken;

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
        var chunkCount = 0;
        var skipped = 0;

        foreach (var (text, idx) in chunks.Select((t, i) => (t, i)))
        {
            // FR-02: documentId + chunkIndex から決定的なチャンク ID を導出（冪等）
            var chunkId = ChunkId.Derive(ev.DocumentId, idx);

            // FR-02 embed: 埋め込み生成（LLM Gateway 経由 / ADR-0013・ADR-0016）。機密区分で送信先・コレクションが決まる。
            var embedding = await embed.EmbedAsync(text, confidentiality, ct);

            // FR-02, FR-05, ADR-0016: fail-closed。高機密でセルフホスト未有効・次元不整合・呼び出し失敗時は
            // 索引しない（外部へ本文を送らず、誤ったコレクション/次元へも書かない）。
            if (!embedding.Embedded)
            {
                skipped++;
                continue;
            }

            // FR-02 index: 機密区分ルーティングが決めたモデル別コレクションへ登録。chunk_index/tags、FR-05 ABAC 属性を保持
            await store.UpsertChunkAsync(embedding.Collection, chunkId, ev.DocumentId, ev.Title, text, idx,
                embedding.Vector, ev.MarkdownUri, ev.Attributes, ev.Tags, ct);
            chunkCount++;
        }

        if (skipped > 0)
        {
            // fail-closed のスキップ（高機密でセルフホスト未有効等）は監査可能にする。
            logger.LogWarning(
                "Ingestion {Id}: {Skipped} chunk(s) skipped (embedding fail-closed; confidentiality={Confidentiality})",
                ev.DocumentId, skipped, confidentiality ?? "(unset)");
        }

        // FR-02: 取り込み完了イベント発行 → 検索反映へ連鎖
        await bus.Publish(new IngestionCompleted(ev.DocumentId, chunkCount, DateTimeOffset.UtcNow), ct);

        logger.LogInformation("Ingestion complete for {Id}: {Count} chunks", ev.DocumentId, chunkCount);
    }
}
