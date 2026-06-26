using FluentAssertions;
using System.Net.Http.Json;

namespace AuthorizationService.Api.Tests;

// FR-05, FR-09, UC-05, ADR-0004: 認可サービス ヘルスチェック
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
    public async Task PostAuthzCheck_Returns200()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/authz/check", new { userId = "u1", documentId = "d1", action = "read" });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
