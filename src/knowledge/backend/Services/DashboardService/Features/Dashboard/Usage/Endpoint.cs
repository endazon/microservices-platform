using DashboardService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DashboardService.Features.Dashboard.Usage;

// FR-10: 日次利用状況（日付 × 種別の件数）。利用状況グラフの入力。
// **［#544］閲覧は管理者・運用者**（計画 §SC-10。裁定 Q19 / Q28）。
// **最終防衛線としてここでも同じ範囲を要求する**（[[IADR-0044]]）。
internal static class DashboardUsageEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/usage", async (int? days, DashboardDbContext db, CancellationToken ct) =>
        {
            var since = DashboardEndpoints.SinceUtc(days);
            var points = await DashboardEndpoints.AggregateUsageAsync(db, since, ct);
            return Results.Ok(points);
        }).WithName("DashboardUsage").RequireAuthorization(p => p.RequireRole(
              PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole))
          .Produces<List<UsagePointDto>>();
    }
}
