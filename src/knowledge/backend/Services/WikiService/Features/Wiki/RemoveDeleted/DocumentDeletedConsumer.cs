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
// 🔴 FR-13, FR-06, [[IADR-0596]] 決定 1・2 (#2102): **削除を版の印に残す**（メタデータの行を消しても印は残す）。削除より前の
// `DocumentUpdated` が遅れて届いても、ページとメタデータを書き戻さない。文書 ID は変換が決定的に採番するので、削除の後に同じ ID で
// 作り直された文書がある —— その版（削除より新しい）を反映済みなら、遅れて届いた古い削除は適用しない。保存が突き合わせで負けたら、
// 保存済みの最新の状態へ Wiki.js を合わせ直す（同期の受け口と同じ。`WikiSyncOrdering`）。
//
// 🔴 #1640: Wiki.js からの撤去は期限（`WikiSyncTimeouts.WikiJs`・既定 15 秒）の下で行う。止まった Wiki.js は、受け口の ct
// （Wolverine の実行期限）で「取り消し」として切られる前に時間切れ（`ConsumerTimeoutException`・計器
// `messaging.consumer.timeout`）として投げる。呼び出し元の取り消しはそのまま外へ出す。
public class DocumentDeletedConsumer(
    WikiDbContext db,
    IWikiJsClient wikiJs,
    IWikiContentReader contentReader,
    ConsumerCallTimeouts calls,
    WikiSyncTimeouts timeouts,
    WikiJsWriteGate writeGate,
    ILogger<DocumentDeletedConsumer> logger) : IPipelineStep<DocumentDeleted>
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "wiki-delete";

    // ADR-0027 / E3a: Wolverine のハンドラ。
    public async Task Handle(DocumentDeleted ev, CancellationToken ct)
    {
        // 🔴 [[IADR-0596]] 決定 2: 古い削除（作り直した後に遅れて届いた削除）は適用しない。
        var mark = await db.SyncMarks.FirstOrDefaultAsync(m => m.DocumentId == ev.DocumentId, ct);
        if (DocumentSyncOrdering.IsStaleDelete(mark, ev.DeletedAt))
        {
            logger.LogInformation(
                "Wiki delete {DocumentId}: skipped a deletion older than the applied version (deleted_at={DeletedAt:O}, applied_at={AppliedAt:O})",
                ev.DocumentId, ev.DeletedAt, mark!.AppliedAt);
            return;
        }

        // 🔴 #1971・[[IADR-0563]]: 同期の受け口と共有の書き込みの門（既定 1 本）を通す。pages.delete も木の再構築を走らせ、
        // 同期の pages.create と並ぶと pageTree の主キーで衝突する。
        await writeGate.RunAsync(calls, StepName, timeouts.WikiJs,
            t => wikiJs.DeletePageAsync(WikiPage.PathFor(ev.DocumentId), t), ct);

        var page = await db.Pages
            .FirstOrDefaultAsync(p => p.DocumentId == ev.DocumentId, ct);
        if (page is not null) db.Pages.Remove(page);
        if (mark is null)
        {
            mark = DocumentSyncMark.For(ev.DocumentId);
            db.SyncMarks.Add(mark);
        }
        mark.RecordDeleted(ev.DeletedAt);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (WikiSyncOrdering.IsMarkConflict(ex))
        {
            // [[IADR-0596]] 決定 3: より新しい版（作り直し）が先に保存されていれば、Wiki.js をその状態へ戻して ack する。
            // そうでなければ（相手が古い更新・同じ削除の並行）投げて再試行へ委ねる（次の試行で削除を保存し直す）。
            if (await WikiSyncOrdering.ReconcileIfOvertakenAsync(db, wikiJs, contentReader, calls, timeouts, writeGate,
                    StepName, ev.DocumentId, m => DocumentSyncOrdering.IsStaleDelete(m, ev.DeletedAt), logger, ct))
                return;
            throw;
        }

        logger.LogInformation("Deleted document {DocumentId} from Wiki.js", ev.DocumentId);
    }
}
