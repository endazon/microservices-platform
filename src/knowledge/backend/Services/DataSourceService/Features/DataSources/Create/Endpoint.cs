using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DataSourceService.Features.DataSources.Create;

// FR-01, SC-06（#628）: 登録は**管理者限定**である（計画 §SC-06「登録・更新・無効化は管理者限定」・
// 裁定 Q19「破壊的操作は管理者限定を維持する」）。グループ既定（admin ＋ operator）は
// **閲覧の下限**を表すので残し、本エンドポイントだけ AdminOnly を積む（AND 合成で実効 admin のみ。
// [[IADR-0128]] 決定 1 が #501 で確立した形）。
internal static class CreateDataSourceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/", async (CreateDataSourceRequest req, DataSourceDbContext db, SyncSchedule schedule) =>
        {
            // IADR-0295 決定 3: 資格情報つきの connectionUri は受け付けない（登録時が第 1 の関門）。
            if (ConnectionUriPolicy.Validate(req.ConnectionUri, existing: null) is { } uriError)
                return Results.BadRequest(new { error = uriError });

            // FR-01, FR-05: 既定 ABAC 属性（機密区分）を伴ってデータソースを登録する。
            var ds = DataSource.Create(req.Name, req.SourceType, req.ConnectionUri,
                req.Config, req.DefaultAttributes);
            db.DataSources.Add(ds);
            await db.SaveChangesAsync();
            return Results.Created($"/datasources/{ds.Id}",
                DataSourceEndpoints.ToResponse(ds, schedule.NextRunAt));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
