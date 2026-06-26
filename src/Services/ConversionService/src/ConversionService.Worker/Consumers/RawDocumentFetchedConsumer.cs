using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace ConversionService.Worker.Consumers;

// FR-12, UC-06: 原本を正規化する（pandoc + LLM）— P0 はスタブ、P1 で実装
public class RawDocumentFetchedConsumer(ILogger<RawDocumentFetchedConsumer> logger)
    : IConsumer<RawDocumentFetched>
{
    public Task Consume(ConsumeContext<RawDocumentFetched> context)
    {
        var msg = context.Message;
        logger.LogInformation(
            "Received RawDocumentFetched: SourceId={SourceId} Path={Path}",
            msg.SourceId, msg.OriginalPath);
        // P1: call pandoc, call LlmGateway for diagram conversion, publish DocumentNormalized
        return Task.CompletedTask;
    }
}
