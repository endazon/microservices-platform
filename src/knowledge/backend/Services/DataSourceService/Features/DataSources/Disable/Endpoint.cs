using DataSourceService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DataSourceService.Features.DataSources.Disable;

// FR-01, SC-06（#628）: 無効化（論理削除）は**管理者限定**である。登録と同じく AdminOnly を積む。
internal static class DisableDataSourceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapDelete("/{id:guid}", async (Guid id, DataSourceDbContext db) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            if (ds is null) return Results.NotFound();
            ds.Disable();
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
