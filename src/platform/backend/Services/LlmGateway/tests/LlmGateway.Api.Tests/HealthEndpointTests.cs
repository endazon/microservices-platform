using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace LlmGateway.Api.Tests;

// FR-04, ADR-0010: LlmGateway エンドポイントテスト
public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ADR-0010: /complete エンドポイント
    [Fact]
    public async Task PostComplete_Returns200()
    {
        var req = new { Prompt = "テスト", MaxTokens = 100 };
        var response = await factory.CreateClient().PostAsJsonAsync("/complete", req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
