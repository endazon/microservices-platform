using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace IngestionService.Worker.Consumers;

// FR-02, UC-04: パース→チャンク→埋め込み→索引登録 — P0 stub, P1 で実装
public class DocumentUpdatedConsumer(ILogger<DocumentUpdatedConsumer> logger)
    : IConsumer<DocumentUpdated>
{
    public Task Consume(ConsumeContext<DocumentUpdated> context)
    {
        var msg = context.Message;
        logger.LogInformation(
            "Received DocumentUpdated: DocumentId={DocumentId} Title={Title}",
            msg.DocumentId, msg.Title);
        // P1: chunk text, call LlmGateway for embeddings, write to Qdrant
        return Task.CompletedTask;
    }
}
