using DashboardService.Domain;
using DashboardService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DashboardService.Features.Dashboard.Summary;

// FR-10: 利用側サマリ（総件数・利用状況・検索傾向）を 1 応答で返す。
// 回答品質は BFF が FeedbackService から付加して DashboardSummaryDto を組み立てる。
//
// 🔴 **検索傾向のしきい値は封筒（DashboardUsageDto）が運ぶ**（ADR-0071 決定 2 / [[IADR-0357]] 決定 2）。
// **検索語の行に持たせない** —— 行はしきい値で伏せた結果として **0 件になり得る**。
// 0 件はしきい値の効果が最も強く出ている状態であり、そこで併記が消えるのは本末転倒である。
internal static class DashboardSummaryEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/summary", async (
            int? days,
            int? top,
            DashboardDbContext db,
            IOptions<SearchTrendOptions> trendOptions,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var since = DashboardEndpoints.SinceUtc(days);
            var usage = await DashboardEndpoints.AggregateUsageAsync(db, since, ct);
            // **添えるのは実際に判定へ使った値**である（構成が不正で既定へ倒したときも倒した後の値）。
            var minCount = DashboardEndpoints.EffectiveMinCount(
                trendOptions, loggerFactory.CreateLogger(typeof(DashboardSummaryEndpoint)));
            var trends = await DashboardEndpoints.AggregateTrendsAsync(
                db, since, DashboardEndpoints.ClampTop(top), minCount, ct);
            var totalSearches = usage.Where(p => p.EventType == UsageEventType.Search).Sum(p => p.Count);
            var totalAnswers = usage.Where(p => p.EventType == UsageEventType.Answer).Sum(p => p.Count);
            return Results.Ok(new DashboardUsageDto(totalSearches, totalAnswers, usage, trends, minCount));
        }).WithName("DashboardSummary").RequireAuthorization(p => p.RequireRole(
              PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole))
          .Produces<DashboardUsageDto>();
    }
}
