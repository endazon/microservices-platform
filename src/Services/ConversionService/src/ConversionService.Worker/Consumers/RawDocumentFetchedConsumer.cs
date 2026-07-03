using ConversionService.Worker.Services;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace ConversionService.Worker.Consumers;

// FR-12, UC-06: 原本取得イベントを受信し正規化変換を行う（pandoc で本文 Markdown 化、
// 図は LLM で PlantUML/Mermaid 化、不可分は画像保持）。
public class RawDocumentFetchedConsumer(
    INormalizationService normalizer,
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

        // FR-12: 本文 Markdown 化 ＋ 図のコード化/画像保持 ＋ オブジェクトストレージ保管。
        // 変換失敗（pandoc/保存の恒久失敗）は例外を送出し、MassTransit の再試行→デッドレターへ委ねる。
        var result = await normalizer.NormalizeAsync(ev, ct);

        // FR-12: 正規化完了イベント発行 → DocumentService が文書を登録し取り込みへ連鎖する。
        // DocumentId は冪等（再変換で同一）。文書管理側で重複登録を避けられる。
        var title = Path.GetFileNameWithoutExtension(ev.OriginalPath);
        await bus.Publish(new DocumentNormalized(
            DocumentId: result.DocumentId,
            SourceId: ev.SourceId,
            Title: title,
            MarkdownUri: result.MarkdownUri,
            AssetUris: [.. result.AssetUris],
            Attributes: ev.Attributes,
            Tags: ev.Tags,
            NormalizedAt: DateTimeOffset.UtcNow), ct);

        logger.LogInformation(
            "Conversion complete for {FetchId}: doc={DocumentId} markdown={Uri} coded={Coded} retained={Retained}",
            ev.FetchId, result.DocumentId, result.MarkdownUri, result.DiagramsCoded, result.DiagramsRetained);
    }
}
