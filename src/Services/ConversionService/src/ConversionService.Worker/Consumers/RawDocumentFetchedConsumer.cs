using ConversionService.Worker.Services;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace ConversionService.Worker.Consumers;

// FR-12, UC-06: 原本取得イベントを受信し正規化変換を行う（pandoc + LLM）
public class RawDocumentFetchedConsumer(
    IConversionService converter,
    IPublishEndpoint bus,
    ILogger<RawDocumentFetchedConsumer> logger) : IConsumer<RawDocumentFetched>
{
    public async Task Consume(ConsumeContext<RawDocumentFetched> context)
    {
        var ev = context.Message;
        var ct = context.CancellationToken;

        logger.LogInformation(
            "Converting raw document: SourceId={SourceId} Path={Path} Type={Type}",
            ev.SourceId, ev.OriginalPath, ev.ContentType);

        // FR-12: pandoc で本文を Markdown 化
        var markdownUri = await converter.ConvertToMarkdownAsync(
            ev.StorageUri, ev.ContentType, ct);

        // FR-12: 正規化完了イベント発行 → DocumentService が文書を登録し取り込みへ連鎖する
        var title = System.IO.Path.GetFileNameWithoutExtension(ev.OriginalPath);
        await bus.Publish(new DocumentNormalized(
            DocumentId: Guid.NewGuid(),
            SourceId: ev.SourceId,
            Title: title,
            MarkdownUri: markdownUri,
            AssetUris: [],
            Attributes: ev.Attributes,
            Tags: ev.Tags,
            NormalizedAt: DateTimeOffset.UtcNow), ct);

        logger.LogInformation("Conversion complete for {FetchId}: markdown={Uri}", ev.FetchId, markdownUri);
    }
}
