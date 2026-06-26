using IngestionService.Worker.Services;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace IngestionService.Worker.Consumers;

// FR-02, UC-04: DocumentUpdated を受信し、チャンク化→埋め込み→Qdrant 登録する
public class DocumentUpdatedConsumer(
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

        if (ev.MarkdownUri is null)
        {
            logger.LogWarning("DocumentUpdated {Id}: MarkdownUri is null, skipping ingestion", ev.DocumentId);
            return;
        }

        logger.LogInformation("Ingesting document {Id} title={Title}", ev.DocumentId, ev.Title);

        // FR-02: 既存チャンクを削除（再インデックス冪等性）
        await store.DeleteByDocumentAsync(ev.DocumentId, ct);

        // 実運用ではオブジェクトストレージから Markdown を読み込む。
        // ここでは URI をプレースホルダーとしてシミュレートする。
        var markdownText = $"# {ev.Title}\n\nコンテンツは {ev.MarkdownUri} から取得します。";

        // FR-02: チャンク化
        var chunks = chunker.Chunk(markdownText);
        var chunkCount = 0;

        foreach (var (text, idx) in chunks.Select((t, i) => (t, i)))
        {
            var chunkId = Guid.NewGuid();
            // FR-02: 埋め込み生成（LLM Gateway 経由）
            var vector = await embed.EmbedAsync(text, ct);

            // FR-05: ABAC 属性をペイロードに保持（検索時フィルタ用）
            await store.UpsertChunkAsync(chunkId, ev.DocumentId, ev.Title, text,
                vector, ev.MarkdownUri, ev.Attributes, ev.Tags, ct);
            chunkCount++;
        }

        // FR-02: 取り込み完了イベント発行
        await bus.Publish(new IngestionCompleted(ev.DocumentId, chunkCount, DateTimeOffset.UtcNow), ct);

        logger.LogInformation("Ingestion complete for {Id}: {Count} chunks", ev.DocumentId, chunkCount);
    }
}
