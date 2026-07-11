using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-10, UC-05: BFF ダッシュボード集約エンドポイント。
// DashboardService（利用状況・検索傾向）と FeedbackService（回答品質）を 1 応答に集約する。
public static class DashboardBffEndpoints
{
    // FR-10: 集計期間の既定・上限（DashboardService・FeedbackService と揃える）。
    private const int DefaultDays = 7;
    private const int MaxDays = 90;

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
            // FR-10: 利用状況と満足率の期間の起点を揃えるため、有効な days を BFF で確定し、
            // DashboardService・FeedbackService の双方に同じ値を渡す（未指定でも両者が既定 7 日で一致する）。
            var effectiveDays = Math.Clamp(days ?? DefaultDays, 1, MaxDays);
            var qs = BuildQuery(effectiveDays, top);

            // DashboardService の集計は AdminOnly のため、利用者の資格情報を後段へ引き継ぐ。
            var dashClient = httpFactory.CreateClient("DashboardService");
            var auth = http.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth))
                dashClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

            var feedbackClient = httpFactory.CreateClient("FeedbackService");

            // 利用側サマリと回答品質を並行取得する（互いに独立）。満足率も同じ days で期間を絞る。
            var usageTask = dashClient.GetAsync($"/dashboard/summary{qs}", ct);
            var qualityTask = feedbackClient.GetAsync($"/feedback/stats?days={effectiveDays}", ct);
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
          .RequireAuthorization(PlatformAuthPolicies.AdminOnly)
          .Produces<DashboardSummaryDto>();

        return app;
    }

    // days（確定済み）と top（未指定は後段の既定に委ねる）を後段へ引き継ぐクエリ文字列を組み立てる。
    private static string BuildQuery(int days, int? top)
    {
        var parts = new List<string> { $"days={days}" };
        if (top is { } t) parts.Add($"top={t}");
        return "?" + string.Join("&", parts);
    }
}
