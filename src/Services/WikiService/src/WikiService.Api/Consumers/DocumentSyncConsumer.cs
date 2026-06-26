using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using WikiService.Api.Domain;
using WikiService.Api.Infrastructure;

namespace WikiService.Api.Consumers;

// FR-13, UC-07: 文書更新イベントを受信し Wiki ページに同期する
public class DocumentSyncConsumer(WikiDbContext db) : IConsumer<DocumentUpdated>
{
    public async Task Consume(ConsumeContext<DocumentUpdated> ctx)
    {
        var ev = ctx.Message;
        if (ev.Status != "published" && ev.Status != "normalized") return;

        var existing = await db.Pages
            .FirstOrDefaultAsync(p => p.DocumentId == ev.DocumentId, ctx.CancellationToken);

        if (existing is null)
        {
            var page = WikiPage.CreateFromDocument(ev.DocumentId, ev.Title,
                ev.MarkdownUri, ev.Attributes, ev.Tags);
            db.Pages.Add(page);
        }
        else
        {
            existing.Sync(ev.Title, ev.MarkdownUri, ev.Attributes, ev.Tags);
        }

        await db.SaveChangesAsync(ctx.CancellationToken);
    }
}
