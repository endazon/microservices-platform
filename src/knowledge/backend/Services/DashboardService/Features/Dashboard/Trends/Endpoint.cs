using DashboardService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DashboardService.Features.Dashboard.Trends;

// FR-10: 検索傾向（よく検索される語の上位）。
internal static class DashboardTrendsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/trends", async (int? days, int? top, DashboardDbContext db, CancellationToken ct) =>
        {
            var since = DashboardEndpoints.SinceUtc(days);
            var trends = await DashboardEndpoints.AggregateTrendsAsync(
                db, since, DashboardEndpoints.ClampTop(top), ct);
            return Results.Ok(trends);
        }).WithName("DashboardTrends").RequireAuthorization(p => p.RequireRole(
              PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole))
          .Produces<List<SearchTrendDto>>();
    }
}
