using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Infrastructure.Extensions;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Endpoints;

// FR-10, UC-05: BFF ダッシュボード集約エンドポイント。
// DashboardService（利用状況・検索傾向）と FeedbackService（回答品質）を 1 応答に集約する。
public static class DashboardBffEndpoints
{
    public static IEndpointRouteBuilder MapDashboardBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/dashboard").WithTags("Dashboard BFF");

        // FR-10: 利用状況・検索傾向・回答品質を 1 つのサマリに集約して返す。
        // 運用・分析向けの管理画面のため、管理者ロールに限定する。
        g.MapGet("/summary", async (
            int? days,
            int? top,
            IHttpClientFactory httpFactory,
            HttpContext http,
            CancellationToken ct) =>
        {
            var qs = BuildQuery(days, top);

            // DashboardService の集計は AdminOnly のため、利用者の資格情報を後段へ引き継ぐ。
            var dashClient = httpFactory.CreateClient("DashboardService");
            var auth = http.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth))
                dashClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

            var feedbackClient = httpFactory.CreateClient("FeedbackService");

            // 利用側サマリと回答品質を並行取得する（互いに独立）。
            var usageTask = dashClient.GetAsync($"/dashboard/summary{qs}", ct);
            var qualityTask = feedbackClient.GetAsync("/feedback/stats", ct);
            await Task.WhenAll(usageTask, qualityTask);

            var usageResp = usageTask.Result;
            if (!usageResp.IsSuccessStatusCode)
                return Results.StatusCode((int)usageResp.StatusCode);
            var usage = await usageResp.Content.ReadFromJsonAsync<DashboardUsageDto>(ct);

            var qualityResp = qualityTask.Result;
            if (!qualityResp.IsSuccessStatusCode)
                return Results.StatusCode((int)qualityResp.StatusCode);
            var quality = await qualityResp.Content.ReadFromJsonAsync<FeedbackStatsDto>(ct);

            if (usage is null || quality is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);

            var summary = new DashboardSummaryDto(
                usage.TotalSearches,
                usage.TotalAnswers,
                usage.UsageTrend,
                usage.TopSearchTerms,
                quality);
            return Results.Ok(summary);
        }).WithName("BffDashboardSummary")
          .RequireAuthorization(KnowledgePlatformAuthPolicies.AdminOnly)
          .Produces<DashboardSummaryDto>();

        return app;
    }

    // days / top を後段へ引き継ぐクエリ文字列を組み立てる（未指定は付けず後段の既定に委ねる）。
    private static string BuildQuery(int? days, int? top)
    {
        var parts = new List<string>();
        if (days is { } d) parts.Add($"days={d}");
        if (top is { } t) parts.Add($"top={t}");
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }
}
