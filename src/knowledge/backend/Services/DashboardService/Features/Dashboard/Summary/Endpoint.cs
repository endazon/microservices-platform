using DashboardService.Domain;
using DashboardService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DashboardService.Features.Dashboard.Summary;

// FR-10: 利用側サマリ（総件数・利用状況・検索傾向）を 1 応答で返す。
// 回答品質は BFF が FeedbackService から付加して DashboardSummaryDto を組み立てる。
internal static class DashboardSummaryEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/summary", async (int? days, int? top, DashboardDbContext db, CancellationToken ct) =>
        {
            var since = DashboardEndpoints.SinceUtc(days);
            var usage = await DashboardEndpoints.AggregateUsageAsync(db, since, ct);
            var trends = await DashboardEndpoints.AggregateTrendsAsync(
                db, since, DashboardEndpoints.ClampTop(top), ct);
            var totalSearches = usage.Where(p => p.EventType == UsageEventType.Search).Sum(p => p.Count);
            var totalAnswers = usage.Where(p => p.EventType == UsageEventType.Answer).Sum(p => p.Count);
            return Results.Ok(new DashboardUsageDto(totalSearches, totalAnswers, usage, trends));
        }).WithName("DashboardSummary").RequireAuthorization(p => p.RequireRole(
              PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole))
          .Produces<DashboardUsageDto>();
    }
}
