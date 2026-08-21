using AwesomeAssertions;
using System.Net;
using System.Net.Http.Json;

namespace AuthorizationService.Api.Tests;

// FR-05, FR-09, UC-05, ADR-0004: 認可サービス エンドポイントテスト
public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-05: ABAC スコープ解決エンドポイント
    [Fact]
    public async Task PostAuthzScope_Returns200()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/authz/scope",
                new { UserId = "u1", UserAttributes = new { department = "engineering" } });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
