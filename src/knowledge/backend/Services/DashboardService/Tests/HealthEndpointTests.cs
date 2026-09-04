using AwesomeAssertions;

namespace DashboardService.Tests;

// FR-10: サービスが独立して稼働する（受け入れ基準④の一部）。
[Trait("TestKind", "Integration")]
public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
