using FluentAssertions;
using Platform.Shared.Contracts.Dtos;
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
                new FeedbackRequest(Guid.NewGuid(), "up", Comment: "助かった"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<FeedbackDto>();
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
            new FeedbackRequest(Guid.NewGuid(), "down"));

        factory.LastFeedbackForwardedAuthorization.Should().Be("Bearer fb-token");
    }

    // FR-08: 満足率の集計が集約される。
    [Fact]
    public async Task GetStats_ReturnsAggregatedStats()
    {
        var stats = await factory.CreateClient()
            .GetFromJsonAsync<FeedbackStatsDto>("/bff/feedback/stats");

        stats.Should().NotBeNull();
        stats!.Total.Should().Be(stats.Up + stats.Down);
    }
}
