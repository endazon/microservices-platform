using DataSourceService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataSourceService.Features.DataSources.List;

// FR-01, UC-04: データソース一覧。
//
// IADR-0053, claude-review #222: 応答では Config 内の秘密（apiToken 等）をマスクする。
// Vault 移行までの暫定措置。admin/operator であっても API 応答で平文の資格情報を露出させない。
//
// SC-06（planning#200 / 裁定 Q15）, IADR-0136: 次回同期は**全ソース同値**である。一覧では
// SyncSchedule を**1 回だけ**読んでその値を全行へ配る（行ごとに読むと境界を跨いだ瞬間に
// 列内で値が割れ、「ソースごとに時刻が違う」という持たないはずの意味を生む）。
internal static class ListDataSourcesEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (DataSourceDbContext db, SyncSchedule schedule) =>
        {
            var nextSyncAt = schedule.NextRunAt;
            return Results.Ok((await db.DataSources.ToListAsync())
                .Select(ds => DataSourceEndpoints.ToResponse(ds, nextSyncAt)));
        });
    }
}
