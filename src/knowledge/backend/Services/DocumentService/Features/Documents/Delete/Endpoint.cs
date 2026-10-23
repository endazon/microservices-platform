using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.Delete;

// FR-06, UC-03, SC-05（#629）: 削除は**人については管理者限定**（計画の列挙「文書の削除」）。
// FR-06, FR-08, 計画 ADR-0119 決定 2 (#1616): **機械クライアントは自分が `owner` の組織文書に限り**削除できる
// （動的束縛。ロールは足さない）。判定は `UpdateMetadata` と同じ 2 段（入口の門 → 取得後の所有者の判定）。
// 削除の伝播範囲（本文・索引まで）は ADR-0057 のとおりで、経路によらず同じ。
// ADR-0027 / E3a: 削除イベントの発行は Wolverine（IDocumentDeletedPublisher 経由）。
// ADR-0057 決定 1 / [[IADR-0296]]: **削除は本文の実体まで及ぶ。** 台帳から逆引きした
// オブジェクトを**先に**消し、その後で DB 行を消す（順序の根拠は同 IADR 決定 3）。
// オブジェクト削除が失敗すれば例外が出て `SaveChangesAsync` へ到達せず、**行は残る**
// （fail-closed。「消したのに実体が残り、参照も失われた」を作らない）。
internal static class DeleteDocumentEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapDelete("/{id:guid}", async (Guid id, DocumentDbContext db,
            IDocumentDeletedPublisher deletedBus, DocumentObjectPurger purger,
            HttpContext http, DocumentReadAccess reads, CancellationToken ct) =>
        {
            // FR-06, SC-05, ADR-0119 決定 2 (#1616): 運用者だけの人は文書の有無に依らず 403（従前の `AdminOnly` と同じ）。
            if (DocumentManageScope.ForbidUnlessAdminOrMachine(http.User) is { } deleteForbidden)
                return deleteForbidden;

            // FR-19, ADR-0036 D-08, ADR-0119 決定 3 (#1629): 個人資料はこの口の対象外（主体を問わず 404）。
            var doc = await DocumentManageScope.FindManageableAsync(db, id, ct);
            if (doc is null) return Results.NotFound();

            // FR-06, ADR-0119 決定 2, ADR-0056 (#1616): 管理者でなければ、自分が owner の機械クライアントだけが消せる。
            if (await DocumentManageScope.DenyUnlessAdminOrMachineOwnerAsync(http.User, doc, reads, db, ct)
                is { } deleteDenied)
                return deleteDenied;
            await purger.PurgeAsync([id], ct);
            db.Documents.Remove(doc);
            // FR-01, ADR-0057 決定 2, [[IADR-0596]] 決定 7 (#2102): 削除の事実（文書 ID と時刻だけ）を残す。変換は文書 ID を決定的に採番するので、
            // 削除より前に作られた `DocumentNormalized` が遅れて届いても台帳へ作り直さない。下流へ伝える削除の時刻も同じ値にする。
            var deletedAt = DateTimeOffset.UtcNow;
            if (await db.DocumentDeletionMarks.FindAsync([id], ct) is { } mark) mark.Advance(deletedAt);
            else db.DocumentDeletionMarks.Add(DocumentDeletionMark.For(id, deletedAt));
            // FR-06, UC-03, [[IADR-0597]] 決定 4 (#2103): 版は並行トークンであり、文書行の DELETE は読んだ版を条件に持つ。
            // 読んだ後に別の更新が版を進めていれば削除は負け、`Update` と同じ 409 `version_conflict` を返す（`expectedVersion` は null）。
            // 🔴 本文の実体は上で既に消している（IADR-0296 決定 3 の順）。行は勝った側の内容で残るので、削除をやり直せば完了する。
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (DocumentVersionConflict.Is(ex))
            {
                return await DocumentVersionConflict.AfterLostSaveAsync(db, id, expectedVersion: null, ct);
            }
            // Issue #88: 削除を下流（Wiki.js 同期・索引・グラフ）へ伝播し、外部システムの実体を撤去する。
            await deletedBus.PublishDeletedAsync(id, deletedAt, ct);
            return Results.NoContent();
        });
    }
}
