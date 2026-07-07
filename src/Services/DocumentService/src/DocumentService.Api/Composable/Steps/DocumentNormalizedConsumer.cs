using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Persistence;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DocumentService.Api.Composable.Steps;

// FR-01, UC-04: ConversionService が発行する DocumentNormalized を購読し、
// 正規化文書をカタログ（正本）へ登録する。登録後 DocumentUpdated を発行して
// 取り込み（IngestionService）・Wiki 同期（WikiService）へ連鎖させる（IADR-0001）。
public class DocumentNormalizedConsumer(
    DocumentDbContext db,
    IPublishEndpoint bus,
    ILogger<DocumentNormalizedConsumer> logger) : IConsumer<DocumentNormalized>
{
    public async Task Consume(ConsumeContext<DocumentNormalized> context)
    {
        var ev = context.Message;
        var ct = context.CancellationToken;

        // FR-01: パイプライン全体で ID を一貫させ、同一イベントの再配信に対して冪等に upsert する。
        var doc = await db.Documents.FindAsync(new object?[] { ev.DocumentId }, ct);
        if (doc is null)
        {
            doc = Document.CreateNormalized(ev.DocumentId, ev.Title, ev.MarkdownUri,
                ev.Attributes, ev.Tags);
            db.Documents.Add(doc);
            logger.LogInformation("Cataloged normalized document {Id} title={Title}",
                ev.DocumentId, ev.Title);
        }
        else
        {
            doc.ApplyNormalized(ev.Title, ev.MarkdownUri, ev.Attributes, ev.Tags);
            logger.LogInformation("Updated cataloged document {Id} from re-normalization",
                ev.DocumentId);
        }

        await db.SaveChangesAsync(ct);

        // FR-01: カタログ登録を後続フロー（取り込み・Wiki 同期）へ通知する。
        await bus.Publish(new DocumentUpdated(
            doc.Id, doc.Title, doc.Status, doc.MarkdownUri,
            doc.Attributes, doc.Tags, doc.UpdatedAt), ct);
    }
}
