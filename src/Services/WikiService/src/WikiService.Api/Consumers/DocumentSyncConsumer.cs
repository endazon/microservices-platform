using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using WikiService.Api.Domain;
using WikiService.Api.Infrastructure;

namespace WikiService.Api.Consumers;

// FR-13, UC-07, ADR-0011, IADR-0020, IADR-0021: 文書更新イベントを受信し Wiki へ同期する。
// 目標構成（IADR-0020/0021）: 正規化 Markdown を Wiki.js へ GraphQL API push で反映する。
// 段階導入（IADR-0020 段2）: 現行は自前 wiki_svc へ upsert する。Wiki.js 同期への置換は後続 PR（要 PoC/ビルド環境）。
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
