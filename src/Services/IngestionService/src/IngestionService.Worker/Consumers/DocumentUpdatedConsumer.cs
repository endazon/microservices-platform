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

        // FR-02: 既存チャンクを削除（再インデックスの冪等性）
        await store.DeleteByDocumentAsync(ev.DocumentId, ct);

        // FR-02 parse: 本文（Markdown）を取得する
        var markdownText = await reader.ReadAsync(ev.MarkdownUri, ev.Title, ct);

        // FR-02 chunk: チャンク化
        var chunks = chunker.Chunk(markdownText);
        var chunkCount = 0;

        foreach (var (text, idx) in chunks.Select((t, i) => (t, i)))
        {
            // FR-02: documentId + chunkIndex から決定的なチャンク ID を導出（冪等）
            var chunkId = ChunkId.Derive(ev.DocumentId, idx);

            // FR-02 embed: 埋め込み生成（LLM Gateway 経由 / ADR-0013）
            var vector = await embed.EmbedAsync(text, ct);

            // FR-02 index: Qdrant へ登録。chunk_index/tags、FR-05 用 ABAC 属性を保持
            await store.UpsertChunkAsync(chunkId, ev.DocumentId, ev.Title, text, idx,
                vector, ev.MarkdownUri, ev.Attributes, ev.Tags, ct);
            chunkCount++;
        }

        // FR-02: 取り込み完了イベント発行 → 検索反映へ連鎖
        await bus.Publish(new IngestionCompleted(ev.DocumentId, chunkCount, DateTimeOffset.UtcNow), ct);

        logger.LogInformation("Ingestion complete for {Id}: {Count} chunks", ev.DocumentId, chunkCount);
    }
}
