using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using WikiService.Domain;
using WikiService.Domain.Ports;
using WikiService.Features.Wiki.SyncDocument;
using WikiService.Infrastructure.Persistence;

namespace WikiService.Features.Wiki;

// FR-13, FR-06, ADR-0027, [[IADR-0596]] 決定 3 (#2102): **保存の突き合わせで負けた受け口が、Wiki.js を保存済みの状態へ合わせ直す。**
//
// 受け口は「印を読む → Wiki.js へ書く → メタデータと印を保存する」の順で動く。2 つのレプリカ（HPA）・更新と削除の 2 本のリスナーが
// 同じ文書を並行に処理すると、古い版の Wiki.js への書き込みが新しい版の書き込みより後に着き得る。印の保存は改訂番号で突き合わせるので、
// 先に保存した側の後から保存する側は必ず負ける。負けた側が**古くなっていたら**（先に保存された版のほうが新しい）、自分の書き込みが
// Wiki.js に残っている恐れがあるので、保存済みの最新の状態（メタデータの行が active なら push・archived ならアーカイブ・行が無ければ削除）へ
// Wiki.js を合わせ直してから ack する。古くなっていなければ（相手が古い・同じ版の並行）false を返し、呼び出し元は投げて再試行へ委ねる。
public static class WikiSyncOrdering
{
    // 突き合わせの負け: 既存の印の改訂番号が読んだ後に動いた（DbUpdateConcurrencyException）か、
    // 同じ文書の印・同期メタデータの行を 2 者が同時に作った（主キー `PK_SyncMarks`・一意索引 `IX_Pages_DocumentId` の 23505。
    // どちらが先に違反するかは挿入の順で決まる）。実 PostgreSQL の結合試験（`WikiSyncMarkPostgresTests`）が引くので公開する。
    public static bool IsMarkConflict(DbUpdateException ex)
        => ex is DbUpdateConcurrencyException
            || ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: WikiDbContext.SyncMarkKeyName or WikiDbContext.DocumentIdIndexName,
            };

    // 突き合わせで負けたら呼ぶ。追跡中の変更（負けた保存の分）は捨てる。合わせ直したら true（呼び出し元は ack する）。
    internal static async Task<bool> ReconcileIfOvertakenAsync(
        WikiDbContext db,
        IWikiJsClient wikiJs,
        IWikiContentReader contentReader,
        ConsumerCallTimeouts calls,
        WikiSyncTimeouts timeouts,
        WikiJsWriteGate writeGate,
        string stepName,
        Guid documentId,
        Func<DocumentSyncMark?, bool> isOvertaken,
        ILogger logger,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var mark = await db.SyncMarks.AsNoTracking().FirstOrDefaultAsync(m => m.DocumentId == documentId, ct);
        if (!isOvertaken(mark)) return false;

        var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.DocumentId == documentId, ct);
        var path = WikiPage.PathFor(documentId);

        Task WikiJsAsync(Func<CancellationToken, Task> call)
            => writeGate.RunAsync(calls, stepName, timeouts.WriteGateWait, timeouts.WikiJs, call, ct);

        if (page is null)
        {
            // 行が無い ＝ 撤去（露出の門）か削除が保存済み。Wiki.js の実体も消す（未存在は成功扱い。deny-closed）。
            await WikiJsAsync(t => wikiJs.DeletePageAsync(path, t));
        }
        else if (page.Status == WikiPageStatus.Archived)
        {
            await WikiJsAsync(t => wikiJs.ArchivePageAsync(path, t));
        }
        else
        {
            string markdown;
            try
            {
                markdown = await calls.RunAsync(stepName, WikiSyncTimeouts.ContentTarget, timeouts.ContentRead,
                    t => contentReader.ReadAsync(page.MarkdownUri, page.Title, t), ct);
            }
            catch (WikiBodyMissingException)
            {
                // 保存済みの版の本文が読めない。古い版の内容を公開のまま残さないよう、ページを非公開にする（deny-closed）。
                await WikiJsAsync(t => wikiJs.ArchivePageAsync(path, t));
                logger.LogWarning(
                    "Wiki sync {DocumentId}: overtaken by a newer version whose body is missing; unpublished the Wiki.js page",
                    documentId);
                return true;
            }

            var wikiPage = DocumentSyncConsumer.ToWikiJsPage(path, page.Title, markdown, page.Tags, page.Attributes, out _);
            await WikiJsAsync(t => wikiJs.UpsertPageAsync(wikiPage, t));
        }

        logger.LogInformation(
            "Wiki sync {DocumentId}: lost the save to a newer version (applied_at={AppliedAt:O}); re-applied the stored state to Wiki.js",
            documentId, mark!.AppliedAt);
        return true;
    }
}
