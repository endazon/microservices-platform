using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Platform.Shared.Contracts.Dtos;

namespace DashboardService.Api.Tests;

// FR-10, UC-05: 利用状況・検索傾向・回答品質ダッシュボードのエンドポイントテスト。
// 集計はグローバル（テスト間で共有すると混ざる）ため、各テストは専用の InMemory DB
// （TestWebApplicationFactory を per-test 生成）で独立させる。
public class DashboardEndpointTests
{
    // T-01: 検索イベントの記録で 201。
    [Fact]
    public async Task RecordSearchEvent_Creates()
    {
        using var factory = new TestWebApplicationFactory();
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "経費規程"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // T-02: 回答イベントの記録で 201（大文字 "ANSWER" も正規化される）。
    [Fact]
    public async Task RecordAnswerEvent_Creates()
    {
        using var factory = new TestWebApplicationFactory();
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/dashboard/events", new UsageEventRequest("ANSWER"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // T-03: 不正な eventType は 400。
    [Fact]
    public async Task InvalidEventType_Returns400()
    {
        using var factory = new TestWebApplicationFactory();
        var resp = await factory.CreateClient()
            .PostAsJsonAsync("/dashboard/events", new UsageEventRequest("click"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // T-04: 利用状況の集計（日次 × 種別の件数）。検索×2・回答×1 を投入し件数を確認する。
    [Fact]
    public async Task Usage_AggregatesByTypeAndDate()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "a"));
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "b"));
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("answer"));

        // レスポンスが null の場合は素の NullReferenceException ではなく明示的な失敗にしてから
        // 非 null 変数へ確定させ、以降の拡張メソッド Where で CS8604 を誘発しないようにする。
        var response = await client.GetFromJsonAsync<List<UsagePointDto>>("/dashboard/usage");
        response.Should().NotBeNull();
        var points = response!;

        points.Where(p => p.EventType == "search").Sum(p => p.Count).Should().Be(2);
        points.Where(p => p.EventType == "answer").Sum(p => p.Count).Should().Be(1);
    }

    // T-05: 検索傾向は件数降順の上位語を返す。
    [Fact]
    public async Task Trends_ReturnsTopTermsByCount()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
            await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "経費"));
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "有給"));

        var trendsResponse = await client.GetFromJsonAsync<List<SearchTrendDto>>("/dashboard/trends");
        trendsResponse.Should().NotBeNull();
        var trends = trendsResponse!;

        trends.Should().HaveCountGreaterThanOrEqualTo(2);
        trends[0].Term.Should().Be("経費");
        trends[0].Count.Should().Be(3);
    }

    // T-06: 検索語は前後空白除去・小文字化で正規化され、同一語として集計される。
    [Fact]
    public async Task Trends_NormalizesTerms()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "Foo"));
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", " foo "));
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "FOO"));

        var trendsResponse = await client.GetFromJsonAsync<List<SearchTrendDto>>("/dashboard/trends");
        trendsResponse.Should().NotBeNull();
        var trends = trendsResponse!;

        trends.Should().ContainSingle();
        trends[0].Term.Should().Be("foo");
        trends[0].Count.Should().Be(3);
    }

    // T-07: サマリは総件数・利用状況・検索傾向を集約して返す。
    [Fact]
    public async Task Summary_AggregatesUsageAndTrends()
    {
        using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "契約"));
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("search", "契約"));
        await client.PostAsJsonAsync("/dashboard/events", new UsageEventRequest("answer"));

        var summaryResponse = await client.GetFromJsonAsync<DashboardUsageDto>("/dashboard/summary");
        summaryResponse.Should().NotBeNull();
        var summary = summaryResponse!;

        summary.TotalSearches.Should().Be(2);
        summary.TotalAnswers.Should().Be(1);
        summary.TopSearchTerms.Should().ContainSingle(t => t.Term == "契約" && t.Count == 2);
    }

    // T-08: 集計 (GET /dashboard/usage) は運用情報のため AdminOnly。非管理ロールは 403。
    [Fact]
    public async Task Usage_WithoutAdminRole_Returns403()
    {
        using var factory = new TestWebApplicationFactory();
        var req = new HttpRequestMessage(HttpMethod.Get, "/dashboard/usage");
        req.Headers.Add(TestAuthHandler.RolesHeader, "viewer"); // 管理者以外のロールを明示。

        var resp = await factory.CreateClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // T-09: イベント記録は認証済みなら管理者以外でも可能（集計の入力を絞らない）。
    [Fact]
    public async Task RecordEvent_AllowedForNonAdmin()
    {
        using var factory = new TestWebApplicationFactory();
        var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/events")
        {
            Content = JsonContent.Create(new UsageEventRequest("search", "検索語"))
        };
        req.Headers.Add(TestAuthHandler.RolesHeader, "viewer");

        var resp = await factory.CreateClient().SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
