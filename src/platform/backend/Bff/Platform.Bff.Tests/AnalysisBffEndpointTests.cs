using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

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

    // FR-07, UC-02: BFF /bff/analysis/analyze が AiAnalysisService の分析結果＋出典を集約して返す
    [Fact]
    public async Task PostAnalyze_ReturnsAggregatedAnswerWithCitations()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/bff/analysis/analyze", new
            {
                instruction = "規程を比較して",
                taskType = "Compare",
                range = new { attributeFilters = new { department = new[] { "sales" } } }
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = await response.Content.ReadFromJsonAsync<AiAnswerDto>();

        answer.Should().NotBeNull();
        answer!.Citations.Should().NotBeEmpty();
    }

    // FR-07, FR-05: 分析でも利用者の資格情報が後段へ引き継がれる（権限外文書を出さない）
    [Fact]
    public async Task PostAnalyze_PropagatesAuthorizationHeaderToBackend()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "analyze-token");

        await client.PostAsJsonAsync("/bff/analysis/analyze",
            new { instruction = "範囲内のみ抽出", taskType = "Extract" });

        factory.LastForwardedAuthorization.Should().Be("Bearer analyze-token");
    }

    // FR-07: 後段（AiAnalysisService）が返した非 2xx を BFF がそのまま透過すること
    // （空 instruction → 400 のバリデーションが後段で行われ、BFF が握り潰さない）。
    // 共有フィクスチャを汚さないよう専用インスタンスでステータスを差し替える。
    [Fact]
    public async Task PostAnalyze_PropagatesBackendBadRequest()
    {
        using var badRequestFactory = new BffTestFactory { StubStatusCode = HttpStatusCode.BadRequest };

        var response = await badRequestFactory.CreateClient()
            .PostAsJsonAsync("/bff/analysis/analyze", new { instruction = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
