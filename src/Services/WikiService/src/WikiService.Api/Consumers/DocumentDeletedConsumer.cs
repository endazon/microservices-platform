using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using WikiService.Api.Domain;
using WikiService.Api.Infrastructure;
using WikiService.Api.Services;

namespace WikiService.Api.Consumers;

// FR-13, UC-07, IADR-0021, Issue #88: 文書削除イベントを受信し Wiki.js の実体を撤去する。
//
// Wiki.js は実コンテンツの実体を保持するため（IADR-0021）、削除の未伝播は社内文書の外部システム
// 残存リスクとなる。本コンシューマは以下を冪等に行う:
//   1. Wiki.js の pages.delete による実体撤去（正準パス doc/<DocumentId>。未存在は成功扱い）。
//   2. wiki_svc 同期メタデータ行の削除（ゲートウェイの一覧・個別から不可視 = 404 存在秘匿を維持）。
// メタデータ未同期の ID でも Wiki.js 側の撤去は試みる（正準パスは DocumentId から導出可能）。
// 失敗は例外を送出し、MassTransit のリトライ／デッドレター（UseKnowledgePlatformRetry）へ委ねる。
public class DocumentDeletedConsumer(
    WikiDbContext db,
    IWikiJsClient wikiJs,
    ILogger<DocumentDeletedConsumer> logger) : IConsumer<DocumentDeleted>
{
    public async Task Consume(ConsumeContext<DocumentDeleted> ctx)
    {
        var ev = ctx.Message;

        await wikiJs.DeletePageAsync(WikiPage.PathFor(ev.DocumentId), ctx.CancellationToken);

        var page = await db.Pages
            .FirstOrDefaultAsync(p => p.DocumentId == ev.DocumentId, ctx.CancellationToken);
        if (page is not null)
        {
            db.Pages.Remove(page);
            await db.SaveChangesAsync(ctx.CancellationToken);
        }

        logger.LogInformation("Deleted document {DocumentId} from Wiki.js", ev.DocumentId);
    }
}
