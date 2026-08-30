using DataSourceService.Infrastructure.Persistence;

namespace DataSourceService.Features.DataSources.GetById;

// FR-01, UC-04: データソースの個別取得。応答の秘密マスクは `ToResponse` が担う（IADR-0053）。
internal static class GetDataSourceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}", async (Guid id, DataSourceDbContext db, SyncSchedule schedule) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            return ds is null
                ? Results.NotFound()
                : Results.Ok(DataSourceEndpoints.ToResponse(ds, schedule.NextRunAt));
        });
    }
}
