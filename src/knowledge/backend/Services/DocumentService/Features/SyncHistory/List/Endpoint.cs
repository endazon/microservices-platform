using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.SyncHistory.List;

// FR-20, UC-11, SC-20 主要素 6, ADR-0037 決定 9, ADR-0099 決定 1〜5, #1446（planning#618 の裁定）:
// 同期履歴の一覧（**本人の行だけ**を実行日時の新しい順に）。
//
// 🔴 **読めるのは本人の記録だけである**（ADR-0099 決定 2。第三者閲覧は未確定）。主体は
// トークンからしか採らず（`PrivateNoteEndpoints.SubjectOf`）、`OwnerId` の一致で絞る ——
// クエリ・本文に主体の口を作らない（他の `/private-notes/*` と同じ作法）。
//
// 🔴 **題名・パス・資料 ID は 1 つも返らない**（決定 5）。返せないのは DTO の項目が無いからでは
// なく、**表に列が無いから**である（`SyncAuditEntry` の注記）。
//
// **表示は 50 件が既定**（決定 4）。`limit` を持つのは画面の都合ではなく、**保持（3 年）と
// 表示（N 件）が別の値である**ことを口の上で分けるためである。「さらに読み込む」は作らない
// （決定 4 は実装設計に委ねたが、要望が出るまで 50 件固定でよい）。
internal static class ListSyncHistoryEndpoint
{
    // ADR-0099 決定 4: 表示の既定。**保持の年数（`SyncAuditEntry.RetentionYears`）と混ぜない。**
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/sync-history", async (int? limit, HttpContext http, DocumentDbContext db,
            CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();

            // 範囲外は **400**（黙って丸めない —— 画面が 500 件を要求して 200 件しか来ない、
            // という食い違いを起こさない）。
            var take = limit ?? DefaultLimit;
            if (take < 1 || take > MaxLimit)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["limit"] = [$"limit は 1〜{MaxLimit} の範囲で指定してください。"],
                });

            var entries = await db.SyncAuditEntries
                .Where(a => a.OwnerId == owner)
                .OrderByDescending(a => a.OccurredAt)
                .Take(take)
                .ToListAsync(ct);

            return Results.Ok(entries.Select(a => a.ToDto()).ToList());
        });
    }
}
