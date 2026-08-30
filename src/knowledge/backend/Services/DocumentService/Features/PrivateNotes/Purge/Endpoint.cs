using DocumentService.Domain.Ports;
// ADR-0057 決定 1 / [[IADR-0296]]: 完全削除は本文の実体まで及ぶ（台帳から逆引きする器）。
using DocumentService.Features.Documents;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Features.PrivateNotes.Purge;

// FR-19, ADR-0037 決定 20: 完全削除（即時・復元不可）。単票も一括も本端点（ids の要素数の差）。
// 対象は**削除済みのみ**（SC-19 の削除済み一覧からの操作）。解放される容量を応答で返す。
//
// ADR-0057 決定 1 / [[IADR-0296]]: **本文の実体も消す。** SC-19 は「いかなる方法でも
// 復元できません」と言い切る画面であり、**実体が残ったまま成功を返してはならない**。
// 台帳（本文 URI ＋ 全版スナップショット URI ＋ 資産 URI）を**行を消す前に**逆引きし、
// オブジェクトを先に消す。失敗すれば例外が出て `SaveChangesAsync` へ到達せず行が残る。
internal static class PurgePrivateNotesEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/purge", async (PurgePrivateNotesRequest req, HttpContext http,
            DocumentDbContext db, IPrivateNoteNotifier notifier, IDocumentDeletedPublisher deletedBus,
            IAuditLogger audit, DocumentObjectPurger purger, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();
            if (req.Ids is not { Count: > 0 })
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ids"] = ["完全削除する資料の ID を 1 件以上指定してください。"]
                });

            var ids = req.Ids.Distinct().ToList();
            var notes = await db.PrivateNotes
                .Where(n => n.OwnerId == owner && ids.Contains(n.DocumentId))
                .ToListAsync(ct);
            // 他者の資料・存在しない ID は区別せず 404（存在秘匿）。
            if (notes.Count != ids.Count) return Results.NotFound();
            if (notes.Any(n => !n.IsDeleted))
                return Results.Conflict(new { error = "not_deleted" });

            var now = DateTimeOffset.UtcNow;
            var freedBytes = notes.Sum(n => n.LatestBytes);
            var docs = await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync(ct);
            // 🔴 **オブジェクトが先、DB 行が後**（[[IADR-0296]] 決定 3）。逆にすると、削除に失敗した
            // 実体を指す値がどこにも残らず、**不可視のまま残留する**。
            await purger.PurgeAsync(ids, ct);
            db.Documents.RemoveRange(docs);           // 版・共有・台帳はカスケード削除
            db.PrivateNotes.RemoveRange(notes);       // InMemory はカスケードしないため明示にも消す
            await db.SaveChangesAsync(ct);
            // 削除を確定させてから使用量を再計算する（先に計算すると purge 分が残って見える）。
            // 使用量が下がって閾値を割れば、警告の発火記録がここで再武装される（FR-22 ②）。
            await PrivateNoteUsage.RecordUsageAndWarnAsync(db, notifier, owner, now, ct);
            await db.SaveChangesAsync(ct);

            // ADR-0037 決定 9・11-①: 監査は「誰が・いつ・何件」。タイトルは記録しない。
            audit.Record("private-note.purge", owner, "granted", $"count={notes.Count}");
            // ADR-0027 / E3a: DocumentDeleted の発行は Wolverine（IDocumentDeletedPublisher 経由）。
            foreach (var id in ids)
                await deletedBus.PublishDeletedAsync(id, now, ct);

            return Results.Ok(new PurgePrivateNotesResponse(notes.Count, freedBytes));
        });
    }
}
