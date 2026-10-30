using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using AiAnalysisService.Domain.Ports;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace AiAnalysisService.Tests.Infrastructure.ExternalServices;

// NFR-02, ADR-0079 決定 1・2, ADR-0076 決定 4, [[IADR-0654]] (#2335):
// **合成の要求が LLM を呼ぶかは要求ごとに決まる。**
//
// 🔴 **60 秒のプローブ（ヘッダ `1`）は LLM を 1 度も呼ばない**（ADR-0079 決定 1。費用の上限は 60 分の間隔で固定する決定 2 の前提）。
// SLO 評価の要求（ヘッダ `slo`）だけが呼ぶ。サービス全体の `AllowLlmEgress` は偽のままでよい。
// 陽性対照（`slo` なら呼ぶ）が無いと、「LLM を 1 度も呼ばない」は「輸送が配線されていない」でも緑になる。
[Trait("TestKind", "Unit")]
public class RagSyntheticLlmEgressTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IHttpContextAccessor Request(string? syntheticHeader)
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        if (syntheticHeader is not null)
            http.HttpContext!.Request.Headers[SyntheticTraffic.HeaderName] = syntheticHeader;
        return http;
    }

    private static IOptions<SyntheticMonitoringOptions> Options(bool allowLlmEgress = false)
        => Microsoft.Extensions.Options.Options.Create(new SyntheticMonitoringOptions { AllowLlmEgress = allowLlmEgress });

    private static async Task RunAsync(string path, RagOrchestratorUnderTest under)
    {
        switch (path)
        {
            case "ask":
                await under.Orchestrator.AskAsync("質問", "u", new Dictionary<string, string>(), null, Ct);
                break;
            case "analyze":
                await under.Orchestrator.AnalyzeAsync(new AnalysisTaskRequest("比較して"), "u",
                    new Dictionary<string, string>(), Ct);
                break;
            default:
                await foreach (var _ in under.Orchestrator.AskStreamAsync("質問", "u", new Dictionary<string, string>(), null, Ct))
                { }
                break;
        }
    }

    private sealed record RagOrchestratorUnderTest(
        AiAnalysisService.Infrastructure.ExternalServices.RagOrchestrator Orchestrator, CountingCompletion Llm);

    private static RagOrchestratorUnderTest Create(string? syntheticHeader, bool allowLlmEgress = false)
    {
        var llm = new CountingCompletion();
        var orchestrator = TestRagOrchestrator.Create(new ScopeOnlyFactory(), Request(syntheticHeader),
            syntheticOptions: Options(allowLlmEgress), completion: llm, search: new OneHitSearch());
        return new RagOrchestratorUnderTest(orchestrator, llm);
    }

    // 🔴 60 秒のプローブ（常時の合成。ヘッダ `1`）は、3 経路とも LLM を 1 度も呼ばない。
    [Theory]
    [InlineData("ask")]
    [InlineData("analyze")]
    [InlineData("stream")]
    public async Task 常時の合成はLLMを呼ばない(string path)
    {
        var under = Create(SyntheticTraffic.HeaderValue);

        await RunAsync(path, under);

        under.Llm.Calls.Should().Be(0, "60 秒のプローブは LLM を呼ばない（ADR-0079 決定 1）");
    }

    // 陽性対照: SLO 評価の合成（ヘッダ `slo`）は、サービス全体の許可が偽でも LLM を呼ぶ。
    [Theory]
    [InlineData("ask")]
    [InlineData("analyze")]
    [InlineData("stream")]
    public async Task SLO評価の合成は要求ごとにLLMを呼ぶ(string path)
    {
        var under = Create(SyntheticTraffic.SloHeaderValue);

        await RunAsync(path, under);

        under.Llm.Calls.Should().Be(1, "SLO 評価の合成は NFR-02 の初回トークンに標本を供給する（ADR-0079 決定 1）");
        under.Llm.LastIsSynthetic.Should().BeTrue("SLO の要求も合成として費用計上から外す（ADR-0076 決定 4）");
    }

    // 対照: 合成でない要求は従前どおり呼ぶ（合成の抑止が実利用へ漏れない）。
    [Fact]
    public async Task 合成でない要求は従前どおりLLMを呼ぶ()
    {
        var under = Create(syntheticHeader: null);

        await RunAsync("stream", under);

        under.Llm.Calls.Should().Be(1);
        under.Llm.LastIsSynthetic.Should().BeFalse();
    }

    // [[IADR-0654]] 決定 4 (#2335): 60 秒の合成の縮退の文は「LLM のトークンではない」として流す（初回トークンの計器へ積まない）。
    // SLO 評価の合成のトークンは LLM のトークンである（積む）。
    [Fact]
    public async Task 合成の縮退の文はLLMのトークンとして流さずSLOのトークンは流す()
    {
        var always = Create(SyntheticTraffic.HeaderValue);
        var alwaysTokens = new List<AskTokenEvent>();
        await foreach (var ev in always.Orchestrator.AskStreamAsync("質問", "u", new Dictionary<string, string>(), null, Ct))
            if (ev is AskTokenEvent t) alwaysTokens.Add(t);

        alwaysTokens.Should().NotBeEmpty("陽性対照: 縮退の文は画面へ届く");
        alwaysTokens.Should().OnlyContain(t => !t.FromModel);

        var slo = Create(SyntheticTraffic.SloHeaderValue);
        var sloTokens = new List<AskTokenEvent>();
        await foreach (var ev in slo.Orchestrator.AskStreamAsync("質問", "u", new Dictionary<string, string>(), null, Ct))
            if (ev is AskTokenEvent t) sloTokens.Add(t);

        sloTokens.Should().ContainSingle().Which.FromModel.Should().BeTrue();
    }

    // 1 件の権限内の文書を返す検索（候補が無いと LLM まで届かない経路を避ける）。
    private sealed class OneHitSearch : IRagSearchTransport
    {
        public Task<IReadOnlyList<SearchResultDto>> SearchAsync(RagSearchQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SearchResultDto>>(
            [
                new SearchResultDto(Guid.NewGuid(), Guid.NewGuid(), "合成の対象文書", "本文の抜粋", 0.9f, null,
                    new Dictionary<string, string> { ["confidentiality"] = "public" }, []),
            ]);
    }

    private sealed class CountingCompletion : ILlmCompletionTransport
    {
        public int Calls { get; private set; }
        public bool LastIsSynthetic { get; private set; }

        public async IAsyncEnumerable<CompletionStreamEvent> StreamAsync(
            CompletionApiRequest request, bool isSynthetic, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            LastIsSynthetic = isSynthetic;
            await Task.Yield();
            yield return new CompletionStreamEvent("回答");
            yield return new CompletionStreamEvent(string.Empty, Done: true, Model: "m");
        }

        public Task<LlmCompletionOutcome> CompleteAsync(CompletionApiRequest request, bool isSynthetic, CancellationToken ct)
        {
            Calls++;
            LastIsSynthetic = isSynthetic;
            return Task.FromResult(new LlmCompletionOutcome(true, new CompletionApiResponse("回答", "m", 1, 1)));
        }
    }

    // `/authz/scope` は全許可、それ以外は非 2xx。
    private sealed class ScopeOnlyFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new ScopeOnlyHandler()) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class ScopeOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/authz/scope"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                            new AccessScopeResponse("u", [], true),
                            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                        Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
