using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-10, UC-05: BFF /bff/dashboard/summary が DashboardService（利用状況・検索傾向）と
// FeedbackService（回答品質）を 1 応答に集約することを検証する。
//
// **［#544］閲覧を運用者へ広げた。** 計画 §SC-10 は「運用者・管理者ロール限定」と定めており、
// 実装だけが `platform-admin` のみに狭かった（裁定 Q19 / Q28）。
// **「広げる」と「広げすぎない」を対で固定する**——片方だけだと検査にならない
// （権限を全開にしても「運用者が通る」テストは緑のままである）。
public class DashboardBffEndpointTests(BffTestFactory factory)
    : IClassFixture<BffTestFactory>
{
    private HttpClient ClientAs(string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, role);
        return client;
    }

    // ★ #544: **運用者が閲覧できる**（受け入れ基準 1 の BFF 層）。
    [Fact]
    public async Task GetSummary_AsOperator_IsAllowed()
    {
        var resp = await ClientAs("platform-operator").GetAsync("/bff/dashboard/summary");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "計画 §SC-10 は閲覧を運用者・管理者ロール限定と定めている");
    }

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

    // FR-10: DashboardService も管理系ロール（admin ＋ operator。#544）を要求するため、資格情報を後段へ伝播する。
    [Fact]
    public async Task GetSummary_PropagatesAuthorizationHeader()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "dash-token");

        await client.GetAsync("/bff/dashboard/summary");

        factory.LastDashboardForwardedAuthorization.Should().Be("Bearer dash-token");
    }

    // T-11: ★ **管理系ロール以外では 403**（受け入れ基準 2「広げすぎない」）。
    //
    // **［#544］名前と趣旨を実態へ揃えた。** 従前は `GetSummary_WithoutAdminRole_Returns403`
    // ＋「管理者以外のロールでは 403」だったが、**運用者は管理者ではないのに 200 になった**ので
    // その言い方は誤りになった。検証しているのは「**管理系ロール（admin / operator）以外**は 403」である。
    //
    // **この対が無いと「広げる」作業は検査にならない**——権限を全開にしても
    // `GetSummary_AsOperator_IsAllowed` は緑のまま通る（変異試験で確認した）。
    [Fact]
    public async Task GetSummary_WithoutPrivilegedRole_Returns403()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/bff/dashboard/summary");
        req.Headers.Add(TestAuthHandler.RolesHeader, "viewer");

        var resp = await factory.CreateClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // T-12: DashboardService が非 2xx を返したら、そのステータスを透過する。
    [Fact]
    public async Task GetSummary_WhenDashboardFails_PropagatesStatus()
    {
        try
        {
            factory.DashboardStubStatusCode = HttpStatusCode.InternalServerError;
            var resp = await factory.CreateClient().GetAsync("/bff/dashboard/summary");
            resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
        finally
        {
            factory.DashboardStubStatusCode = HttpStatusCode.OK;
        }
    }

    // T-13: FeedbackService（回答品質）が非 2xx を返したら、そのステータスを透過する。
    [Fact]
    public async Task GetSummary_WhenFeedbackStatsFails_PropagatesStatus()
    {
        try
        {
            factory.FeedbackStatsStubStatusCode = HttpStatusCode.ServiceUnavailable;
            var resp = await factory.CreateClient().GetAsync("/bff/dashboard/summary");
            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            factory.FeedbackStatsStubStatusCode = HttpStatusCode.OK;
        }
    }

    // T-14: 後段が 2xx でも本文が null なら 502（BadGateway）を返す。
    [Fact]
    public async Task GetSummary_WhenDashboardBodyNull_Returns502()
    {
        try
        {
            factory.DashboardReturnsNullBody = true;
            var resp = await factory.CreateClient().GetAsync("/bff/dashboard/summary");
            resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
        finally
        {
            factory.DashboardReturnsNullBody = false;
        }
    }
}
