using FluentAssertions;

namespace RetrievalService.Api.Tests;

// FR-03, UC-01: 検索サービス ヘルスチェック
public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSearch_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/search?q=test&limit=5");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
