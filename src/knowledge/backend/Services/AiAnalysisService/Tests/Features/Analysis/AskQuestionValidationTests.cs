using System.Net;
using System.Net.Http.Json;
using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Features.Analysis;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AiAnalysisService.Tests.Features.Analysis;

// SC-01, FR-04, UC-01 (#2321): 質問の必須と最大長を後段の `/ask`・`/ask/stream` で検証する。
//
// 🔴 **測るのは「400 になること」と「RAG（＝LLM）が呼ばれないこと」の両方である。** 400 を返しても
// 後ろで呼んでいれば、上限なしに LLM へ届くという問題は残る。
// 記録はスタブのインスタンスの状態であり、`IClassFixture` がクラスごとに factory を作るので他クラスと共有しない。
[Trait("TestKind", "Integration")]
public class AskQuestionValidationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private StubRagOrchestrator Stub => (StubRagOrchestrator)factory.Services.GetRequiredService<IRagOrchestrator>();

    public static TheoryData<string, string> Rejected => new()
    {
        { "/analysis/ask", new string('あ', AskRequestValidator.MaxQuestionLength + 1) },
        { "/analysis/ask/stream", new string('あ', AskRequestValidator.MaxQuestionLength + 1) },
        { "/analysis/ask", "   " },
        { "/analysis/ask/stream", "" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task 上限を超える質問と空の質問は400でRAGを呼ばない(string path, string question)
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = Stub;
        var (ask, stream) = (stub.AskCalls, stub.StreamCalls);

        var resp = await factory.CreateClient().PostAsJsonAsync(path, new { question }, ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json",
            "SSE のヘッダを書く前に拒む（/ask/stream も JSON の 400）");
        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.Should().BeOneOf(AskRequestValidator.QuestionRequiredMessage, AskRequestValidator.QuestionTooLongMessage);
        (stub.AskCalls, stub.StreamCalls).Should().Be((ask, stream), "弾いた質問は RAG・LLM へ届かない");
    }

    // 陽性対照: 上限ちょうどは通り、RAG が呼ばれる（境界を片側だけ見ると off-by-one を見逃す）。
    [Theory]
    [InlineData("/analysis/ask")]
    [InlineData("/analysis/ask/stream")]
    public async Task 上限ちょうどの質問は通りRAGを呼ぶ(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = Stub;
        var before = stub.AskCalls + stub.StreamCalls;

        var resp = await factory.CreateClient().PostAsJsonAsync(
            path, new { question = new string('あ', AskRequestValidator.MaxQuestionLength) }, ct);
        await resp.Content.ReadAsStringAsync(ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (stub.AskCalls + stub.StreamCalls).Should().Be(before + 1);
    }

    private sealed record ErrorBody(string Error);
}
