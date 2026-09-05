using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiAnalysisService.Domain;
using AiAnalysisService.Features.Analysis.Analyze;
using AwesomeAssertions;

namespace AiAnalysisService.Tests.Features.Analysis.Analyze;

// FR-07, UC-02 / IADR-0371 決定 2 / IADR-0393: 検証を FluentValidation へ移した際、
// **HTTP の面で応答が変わっていない**ことを固定する。
//
// 🔴 **既存の `PostAnalyze_EmptyInstruction_Returns400` は状態コードしか見ていない。**
// 400 のままメッセージだけが変わる退行（あるいは検証器の規則順が入れ替わって別の理由が返る退行）は、
// そこでは捕まらない。本クラスは**本文の `error` 文字列**を端点越しに固定する。
[Trait("TestKind", "Integration")]
public class AnalyzeResponseContractTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task BlankInstruction_Returns400WithOriginalBody()
        => await AssertBadRequestBody(
            new { instruction = "   " },
            AnalyzeRequestValidator.InstructionRequiredMessage);

    [Fact]
    public async Task TooLongInstruction_Returns400WithOriginalBody()
        => await AssertBadRequestBody(
            new { instruction = new string('あ', AnalysisPromptBuilder.MaxInstructionLength + 1) },
            AnalyzeRequestValidator.InstructionTooLongMessage);

    // 複数違反しても、返るのは**最初の規則**の本文である（移送前のガード節と同じ）。
    [Fact]
    public async Task MultipleViolations_ReturnsFirstRuleBody()
        => await AssertBadRequestBody(
            new { instruction = new string(' ', AnalysisPromptBuilder.MaxInstructionLength + 1) },
            AnalyzeRequestValidator.InstructionRequiredMessage);

    private async Task AssertBadRequestBody(object request, string expectedMessage)
    {
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/analysis/analyze", request,
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("error").GetString().Should().Be(expectedMessage);
    }
}
