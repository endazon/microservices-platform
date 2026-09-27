using Platform.Shared.Infrastructure.Foundation.Messaging;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using WikiService.Domain;
using WikiService.Infrastructure.Persistence;
using WikiService.Domain.Ports;

namespace WikiService.Features.Wiki.RemoveDeleted;

// FR-13, UC-07, IADR-0021, Issue #88: 文書削除イベントを受信し Wiki.js の実体を撤去する。
//
// Wiki.js は実コンテンツの実体を保持するため（IADR-0021）、削除の未伝播は社内文書の外部システム
// 残存リスクとなる。本コンシューマは以下を冪等に行う:
//   1. Wiki.js の pages.delete による実体撤去（正準パス doc/<DocumentId>。未存在は成功扱い）。
//   2. wiki_svc 同期メタデータ行の削除（ゲートウェイの一覧・個別から不可視 = 404 存在秘匿を維持）。
// メタデータ未同期の ID でも Wiki.js 側の撤去は試みる（正準パスは DocumentId から導出可能）。
// 失敗は例外を送出し、Wolverine のリトライ／デッドレター（UsePlatformMessagingDefaults）へ委ねる。
//
// 🔴 ADR-0027 / E3a: **購読は Wolverine へ移した**（IPipelineStep<DocumentDeleted>・IADR-0239）。
// E3b で DocumentSyncConsumer（DocumentUpdated）も Wolverine へ移り、本サービスに MassTransit は残っていない。
//
// 🔴 #1640: Wiki.js からの撤去は期限（`WikiSyncTimeouts.WikiJs`・既定 15 秒）の下で行う。止まった Wiki.js は、受け口の ct
// （Wolverine の実行期限）で「取り消し」として切られる前に時間切れ（`ConsumerTimeoutException`・計器
// `messaging.consumer.timeout`）として投げる。呼び出し元の取り消しはそのまま外へ出す。
public class DocumentDeletedConsumer(
    WikiDbContext db,
    IWikiJsClient wikiJs,
    ConsumerCallTimeouts calls,
    WikiSyncTimeouts timeouts,
    ILogger<DocumentDeletedConsumer> logger) : IPipelineStep<DocumentDeleted>
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "wiki-delete";

    // ADR-0027 / E3a: Wolverine のハンドラ。
    public async Task Handle(DocumentDeleted ev, CancellationToken ct)
    {
        await calls.RunAsync(StepName, WikiSyncTimeouts.WikiJsTarget, timeouts.WikiJs,
            t => wikiJs.DeletePageAsync(WikiPage.PathFor(ev.DocumentId), t), ct);

        var page = await db.Pages
            .FirstOrDefaultAsync(p => p.DocumentId == ev.DocumentId, ct);
        if (page is not null)
        {
            db.Pages.Remove(page);
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Deleted document {DocumentId} from Wiki.js", ev.DocumentId);
    }
}
