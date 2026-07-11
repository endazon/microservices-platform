using Platform.Shared.Infrastructure.Foundation.Pipeline;
using ConversionService.Worker.Foundation.Ports;
using ConversionService.Worker.Foundation.Services;
using ConversionService.Worker.Foundation.Domain;
using ConversionService.Worker.Foundation.Jobs;
using ConversionService.Worker.Composable.Adapters;
using Knowledge.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace ConversionService.Worker.Composable.Steps;

// FR-12, UC-06: 原本取得イベントを受信し正規化変換を行う（pandoc で本文 Markdown 化、
// 図は LLM で PlantUML/Mermaid 化、不可分は画像保持）。
// SC-07: 変換状況の可視化（成功／失敗）と人手補正のため、ライフサイクルを IConversionJobStore に記録する。
public class RawDocumentFetchedConsumer(
    INormalizationService normalizer,
    IPublishEndpoint bus,
    IConversionJobStore jobs,
    ILogger<RawDocumentFetchedConsumer> logger) : IConsumer<RawDocumentFetched>, IPipelineStep
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "convert";

    public async Task Consume(ConsumeContext<RawDocumentFetched> context)
    {
        var ev = context.Message;
        var ct = context.CancellationToken;

        logger.LogInformation(
            "Converting raw document: SourceId={SourceId} Path={Path} Type={Type}",
            ev.SourceId, ev.OriginalPath, ev.ContentType);

        // SC-07: 変換開始を記録（受信・再試行の都度）。
        await jobs.StartAsync(ev, ct);

        try
        {
            // FR-12: 本文 Markdown 化 ＋ 図のコード化/画像保持 ＋ オブジェクトストレージ保管。
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

            // SC-07: 成功を記録。
            await jobs.SucceedAsync(ev.FetchId, result.DocumentId, result.MarkdownUri, ct);

            logger.LogInformation(
                "Conversion complete for {FetchId}: doc={DocumentId} markdown={Uri} coded={Coded} retained={Retained}",
                ev.FetchId, result.DocumentId, result.MarkdownUri, result.DiagramsCoded, result.DiagramsRetained);
        }
        catch (Exception ex)
        {
            // SC-07: 失敗を記録してから再送出する。変換失敗（pandoc/保存の恒久失敗）は MassTransit の
            // 再試行→デッドレターへ委ねる（記録は状況可視化・人手補正のためで、リトライ挙動は変えない）。
            // 例外メッセージは admin/operator UI に露出するため、単一行・長さ上限に要約する（内部詳細の露出抑制）。
            // 失敗記録は best-effort（CancellationToken.None）で行い、元例外を消さずに再送出する
            // （ct 失効時に SaveChanges がキャンセル例外を投げて元の変換失敗を隠さないため）。
            await jobs.FailAsync(ev.FetchId, SummarizeError(ex.Message), CancellationToken.None);
            throw;
        }
    }

    // SC-07: 変換失敗メッセージを 1 行・最大 300 文字へ丸める（改行・冗長なスタック様文言の UI 露出を避ける）。
    private static string SummarizeError(string message)
    {
        var firstLine = message.Replace("\r", " ").Replace("\n", " ").Trim();
        const int max = 300;
        return firstLine.Length <= max ? firstLine : firstLine[..max] + "…";
    }
}
