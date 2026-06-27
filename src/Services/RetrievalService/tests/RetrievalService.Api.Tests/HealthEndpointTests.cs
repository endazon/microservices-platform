using FluentAssertions;
using System.Net;
using System.Net.Http.Json;

namespace RetrievalService.Api.Tests;

// FR-03, UC-01: 検索サービス エンドポイントテスト
public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // FR-03, ADR-0009: POST /search エンドポイント
    [Fact]
    public async Task PostSearch_Returns200()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/search",
                new { Query = "ナレッジ管理", TopK = 3 });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
