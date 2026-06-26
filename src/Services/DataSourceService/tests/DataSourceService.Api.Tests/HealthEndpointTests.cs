using FluentAssertions;

namespace DataSourceService.Api.Tests;

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
    public async Task GetDataSources_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/datasources");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
