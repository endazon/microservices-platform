using AwesomeAssertions;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-03, FR-04: BFF ヘルスチェック
public class HealthEndpointTests(BffTestFactory factory)
    : IClassFixture<BffTestFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
