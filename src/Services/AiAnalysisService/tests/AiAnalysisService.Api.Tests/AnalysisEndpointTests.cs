using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using System.Net.Http.Json;

namespace AiAnalysisService.Api.Tests;

// FR-04, UC-01, UC-02: /analysis/ask が回答本文と番号付き出典を返すことを検証する
public class AnalysisEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task PostAnalysisAsk_ReturnsAnswerWithCitations()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/analysis/ask", new { question = "規程の更新手順は？" });

        response.IsSuccessStatusCode.Should().BeTrue();
        var answer = await response.Content.ReadFromJsonAsync<AiAnswerDto>();

        answer.Should().NotBeNull();
        answer!.Answer.Should().NotBeNullOrWhiteSpace();
        answer.Citations.Should().NotBeEmpty();
        answer.Citations[0].Number.Should().Be(1);
        answer.Citations[0].SourceUri.Should().NotBeNullOrEmpty();
    }
}
