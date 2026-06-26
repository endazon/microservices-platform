using FluentAssertions;

namespace WikiService.Api.Tests;

// FR-13, UC-07: Wiki サービス ヘルスチェック
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
    public async Task GetWikiPages_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/wiki/pages");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
