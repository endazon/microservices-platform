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
        // FR-10, SC-10（#544）: **閲覧は管理者・運用者**である。計画 §SC-10 は
        // 「運用者・管理者ロール限定」（モックの「運用」バッジ準拠）と定めており、
        // **実装だけが `platform-admin` のみに狭かった**（裁定 Q19 / Q28。環流 planning#198・planning#199）。
        // **参照専用であり、書き込み権限を広げるものではない**——利用イベントの記録
        // （`POST /dashboard/events`）は本作業で触らない。
        // **後段（`DashboardEndpoints`）にも同じ範囲を置いてある**（[[IADR-0044]] の多層防御）——
        // 片側だけだと「BFF 迂回で通る」か「画面だけ 403 になる」のどちらかが起きる。
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

            // DashboardService の集計も管理系ロール（admin ＋ operator。#544）を要求するため、
            // 利用者の資格情報を後段へ引き継ぐ。
            var dashClient = httpFactory.CreateClient("DashboardService");
            var auth = http.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth))
                dashClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

            // #948: **FeedbackService へも同じく引き継ぐ。** `/feedback/stats` は 2026-08-10 に
            // RequireRole(admin, operator) を獲得した（#521 / IADR-0158）が、**この呼び出しだけが
            // 取り残された**。無資格の呼び出しは challenge され、その 401 を下の非 2xx 透過が
            // そのまま返すため、利用者には「有効なトークンなのに 401」として現れていた。
            var feedbackClient = httpFactory.CreateClient("FeedbackService");
            if (!string.IsNullOrEmpty(auth))
                feedbackClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

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
          .RequireAuthorization(p => p.RequireRole(
              PlatformAuthPolicies.AdminRole,
              PlatformAuthPolicies.OperatorRole))
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
