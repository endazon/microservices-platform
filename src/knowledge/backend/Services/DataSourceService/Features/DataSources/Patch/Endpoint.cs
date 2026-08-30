using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DataSourceService.Features.DataSources.Patch;

// FR-01, UC-04, SC-06（Q16 / #534）: 部分更新。**null の項目は現状維持**である。
// 接続先だけ・認証情報だけを差し替える日常運用を、他項目を読んで書き戻す往復なしに行えるようにする
// ——往復させると応答のマスク済みの値（*** ・IADR-0053）を書き戻して秘密を破壊する。
internal static class PatchDataSourceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPatch("/{id:guid}", async (Guid id, PatchDataSourceRequest req, DataSourceDbContext db,
            SyncSchedule schedule) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            if (ds is null) return Results.NotFound();

            // IADR-0295 決定 3: PATCH は「読んで一部だけ直して送り返す」経路そのものである。
            // null（＝現状維持）は検証を素通しする（`Validate` が空を受理する）。
            if (ConnectionUriPolicy.Validate(req.ConnectionUri, ds.ConnectionUri) is { } uriError)
                return Results.BadRequest(new { error = uriError });

            ds.Patch(req.Name, req.SourceType, req.ConnectionUri, req.Config, req.DefaultAttributes);
            await db.SaveChangesAsync();
            return Results.Ok(DataSourceEndpoints.ToResponse(ds, schedule.NextRunAt));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
