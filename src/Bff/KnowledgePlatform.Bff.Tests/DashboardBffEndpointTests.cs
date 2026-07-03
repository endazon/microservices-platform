using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Tests;

// FR-10, UC-05: BFF /bff/dashboard/summary が DashboardService（利用状況・検索傾向）と
// FeedbackService（回答品質）を 1 応答に集約することを検証する。
public class DashboardBffEndpointTests(BffTestFactory factory)
    : IClassFixture<BffTestFactory>
{
    // T-10: サマリが利用状況・検索傾向・回答品質を集約して返す。
    [Fact]
    public async Task GetSummary_AggregatesUsageAndQuality()
    {
        var summary = await factory.CreateClient()
            .GetFromJsonAsync<DashboardSummaryDto>("/bff/dashboard/summary");

        summary.Should().NotBeNull();
        // DashboardService スタブ由来（利用状況・検索傾向）
        summary!.TotalSearches.Should().Be(5);
        summary.TotalAnswers.Should().Be(3);
        summary.UsageTrend.Should().HaveCount(2);
        summary.TopSearchTerms.Should().Contain(t => t.Term == "経費" && t.Count == 4);
        // FeedbackService スタブ由来（回答品質）
        summary.Quality.Total.Should().Be(summary.Quality.Up + summary.Quality.Down);
    }

    // FR-10: DashboardService は AdminOnly のため、資格情報を後段へ伝播する。
    [Fact]
    public async Task GetSummary_PropagatesAuthorizationHeader()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "dash-token");

        await client.GetAsync("/bff/dashboard/summary");

        factory.LastDashboardForwardedAuthorization.Should().Be("Bearer dash-token");
    }

    // T-11: 管理者以外のロールでは 403（運用・分析向けの管理画面）。
    [Fact]
    public async Task GetSummary_WithoutAdminRole_Returns403()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/bff/dashboard/summary");
        req.Headers.Add(TestAuthHandler.RolesHeader, "viewer");

        var resp = await factory.CreateClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
