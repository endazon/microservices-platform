using AwesomeAssertions;
using System.Net.Http.Json;

namespace AiAnalysisService.Api.Tests;

// FR-04, FR-07, UC-01, UC-02: AI 分析サービス ヘルスチェック
public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostAnalysisAsk_Returns200()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/analysis/ask", new { question = "test question" }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
