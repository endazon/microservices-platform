using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-08, UC-01: BFF /bff/feedback が FeedbackService へ集約することを検証する。
public class FeedbackBffEndpointTests(BffTestFactory factory)
    : IClassFixture<BffTestFactory>
{
    // T-10: フィードバック送信が後段へ委譲され、201 を返す。
    [Fact]
    public async Task PostFeedback_Delegates()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/bff/feedback",
                new FeedbackRequest(Guid.NewGuid(), "up", Comment: "助かった"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<FeedbackDto>(TestContext.Current.CancellationToken);
        dto!.Rating.Should().Be("up");
    }

    // FR-08: 送信者特定のため Authorization を後段へ伝播する。
    [Fact]
    public async Task PostFeedback_PropagatesAuthorizationHeader()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "fb-token");

        await client.PostAsJsonAsync("/bff/feedback",
            new FeedbackRequest(Guid.NewGuid(), "down"), TestContext.Current.CancellationToken);

        factory.LastFeedbackForwardedAuthorization.Should().Be("Bearer fb-token");
    }

    // FR-08: 満足率の集計が集約される。
    [Fact]
    public async Task GetStats_ReturnsAggregatedStats()
    {
        var stats = await factory.CreateClient()
            .GetFromJsonAsync<FeedbackStatsDto>("/bff/feedback/stats", TestContext.Current.CancellationToken);

        stats.Should().NotBeNull();
        stats!.Total.Should().Be(stats.Up + stats.Down);
    }

    // ── #521: 端点認可（計画 FR-08 確定 2026-08-07・裁定依頼 planning#236 案 2）
    //
    // 従前この群には認可が無く、**無認証で投稿も統計取得もできた**。計画の受け入れ基準
    // （`02_requirements:209`）は「投稿端点が無認証で 401。統計は認証済みでも運用者・管理者以外は 403」。
    // 後段（FeedbackService）にも同じ認可がある（[[IADR-0044]] 多層防御）。

    // T-15（#521）: 投稿は無認証で 401（匿名投稿を許さない）。
    [Fact]
    public async Task PostFeedback_WhenAnonymous_IsUnauthorized()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var resp = await client.PostAsJsonAsync("/bff/feedback",
            new FeedbackRequest(Guid.NewGuid(), "up"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // T-18（#521）: 投稿にロールは要らない（認証さえあれば一般利用者も送れる）。
    // **狭めすぎていないことの側**——ロールまで要求すると FR-08 が成り立たなくなる。
    [Fact]
    public async Task PostFeedback_AsNonPrivilegedRole_IsAllowed()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var resp = await client.PostAsJsonAsync("/bff/feedback",
            new FeedbackRequest(Guid.NewGuid(), "up"), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // T-17（#521）: 統計は無認証で 401。
    [Fact]
    public async Task Stats_WhenAnonymous_IsUnauthorized()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var resp = await client.GetAsync("/bff/feedback/stats", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // T-16（#521）: 統計は認証済みでも運用者・管理者以外は 403（[[IADR-0039]] 決定 3: 401 と区別）。
    [Fact]
    public async Task Stats_AsNonPrivilegedRole_IsForbidden()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var resp = await client.GetAsync("/bff/feedback/stats", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // T-19（#521）: 運用者は統計を取得できる（SC-10 の閲覧ロールと揃える。#544 と同じ線）。
    // 403 の側と**対**で固定する——片側だけだと「全部拒否」でも緑になる。
    [Fact]
    public async Task Stats_AsOperator_IsAllowed()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");

        var resp = await client.GetAsync("/bff/feedback/stats", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // T-17 / #948: ★ **統計取得も FeedbackService へ資格情報を伝播する。**
    //
    // `/feedback/stats` は #521 で RequireRole(admin, operator) を獲得したが、**BFF 側の 2 つの
    // 呼び出しはどちらも取り残されていた** —— ここ（`GET /bff/feedback/stats`）と、
    // ダッシュボード集約（`GET /bff/dashboard/summary`。#948 の症状として報告された方）である。
    // **投稿（POST）だけが伝播していた**ため、その 1 本を見て「伝播している」と読めてしまう形だった。
    [Fact]
    public async Task GetStats_PropagatesAuthorizationHeaderToFeedbackService()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "stats-token");

        await client.GetAsync("/bff/feedback/stats", TestContext.Current.CancellationToken);

        factory.LastFeedbackForwardedAuthorization.Should().Be("Bearer stats-token");
    }
}
