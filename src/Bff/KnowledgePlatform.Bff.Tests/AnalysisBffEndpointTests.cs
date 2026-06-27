using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace KnowledgePlatform.Bff.Tests;

// FR-04, UC-01, UC-02: BFF /bff/analysis/ask が AiAnalysisService の回答＋出典を集約して返すことを検証する
public class AnalysisBffEndpointTests(BffTestFactory factory)
    : IClassFixture<BffTestFactory>
{
    [Fact]
    public async Task PostAsk_ReturnsAggregatedAnswerWithCitations()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/bff/analysis/ask", new { question = "経費精算の締め日は？" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = await response.Content.ReadFromJsonAsync<AiAnswerDto>();

        answer.Should().NotBeNull();
        answer!.Citations.Should().NotBeEmpty();
        answer.Citations[0].SourceUri.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PostAsk_PropagatesAuthorizationHeaderToBackend()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        await client.PostAsJsonAsync("/bff/analysis/ask", new { question = "権限内のみ" });

        // FR-05: 権限の無い文書を除外するため利用者の資格情報が後段へ引き継がれること
        factory.LastForwardedAuthorization.Should().Be("Bearer test-token");
    }
}
