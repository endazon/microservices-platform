using DashboardService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DashboardService.Features.Dashboard.Trends;

// FR-10: 検索傾向（よく検索される語の上位）。
//
// **応答は配列のままである**（ADR-0071 決定 2 のしきい値併記は封筒 DTO 側が担う。[[IADR-0354]] 決定 2）。
// 包み直すと `GET /dashboard/trends` の応答の形が変わり**破壊的変更**になるが、
// 併記が要るのは画面であり、画面が読むのは `/bff/dashboard/summary` である。
// **ふるい落とし自体はここにも等しく効く**（下の `minCount`）。
internal static class DashboardTrendsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/trends", async (
            int? days,
            int? top,
            DashboardDbContext db,
            IOptions<SearchTrendOptions> trendOptions,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var since = DashboardEndpoints.SinceUtc(days);
            var minCount = DashboardEndpoints.EffectiveMinCount(
                trendOptions, loggerFactory.CreateLogger(typeof(DashboardTrendsEndpoint)));
            var trends = await DashboardEndpoints.AggregateTrendsAsync(
                db, since, DashboardEndpoints.ClampTop(top), minCount, ct);
            return Results.Ok(trends);
        }).WithName("DashboardTrends").RequireAuthorization(p => p.RequireRole(
              PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole))
          .Produces<List<SearchTrendDto>>();
    }
}
