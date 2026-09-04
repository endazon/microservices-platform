using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using System.Net.Http.Json;

namespace AiAnalysisService.Tests.Features.Analysis;

// FR-04, UC-01, UC-02: /analysis/ask が回答本文と番号付き出典を返すことを検証する
[Trait("TestKind", "Integration")]
public class AnalysisEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task PostAnalysisAsk_ReturnsAnswerWithCitations()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/analysis/ask", new { question = "規程の更新手順は？" }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
        var answer = await response.Content.ReadFromJsonAsync<AiAnswerDto>(TestContext.Current.CancellationToken);

        answer.Should().NotBeNull();
        answer!.Answer.Should().NotBeNullOrWhiteSpace();
        answer.Citations.Should().NotBeEmpty();
        answer.Citations[0].Number.Should().Be(1);
        answer.Citations[0].SourceUri.Should().NotBeNullOrEmpty();
    }

    // FR-07, UC-02: /analysis/analyze が指定データ範囲での分析結果＋出典を返す。
    [Fact]
    public async Task PostAnalyze_ReturnsAnswerWithCitations()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/analysis/analyze", new
            {
                instruction = "2025 年の経費規程を比較して",
                taskType = "Compare",
                range = new { attributeFilters = new { year = new[] { "2025" } } }
            }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
        var answer = await response.Content.ReadFromJsonAsync<AiAnswerDto>(TestContext.Current.CancellationToken);

        answer.Should().NotBeNull();
        answer!.Answer.Should().NotBeNullOrWhiteSpace();
        answer.Citations.Should().NotBeEmpty();
    }

    // FR-08, UC-01: 回答にはフィードバック紐付け用の一意な AnswerId が付与される。
    [Fact]
    public async Task PostAnalysisAsk_AnswerHasAnswerId()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/analysis/ask", new { question = "経費規程は？" }, TestContext.Current.CancellationToken);

        var answer = await response.Content.ReadFromJsonAsync<AiAnswerDto>(TestContext.Current.CancellationToken);
        answer!.AnswerId.Should().NotBe(Guid.Empty);
    }

    // FR-07: 指示が空の依頼は 400（バリデーション）。
    [Fact]
    public async Task PostAnalyze_RejectsEmptyInstruction()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/analysis/analyze", new { instruction = "" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }
}
