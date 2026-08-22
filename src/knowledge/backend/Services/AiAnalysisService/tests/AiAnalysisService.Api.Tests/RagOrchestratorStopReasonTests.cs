using System.Net;
using System.Text;
using AiAnalysisService.Api.Foundation.Services;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Api.Tests;

// FR-04, FR-11, ADR-0025, IADR-0104 (#379): ゲートウェイが stopReason="refusal" を返したとき、
// 呼び出し側が「モデルが拒否した」と「送信したが空応答」を取り違えないことを検証する。
// 拒否は sent=true で返る（越境は成立している）ため、sent だけを見る従来の分岐では区別できない。
public class RagOrchestratorStopReasonTests
{
    // 拒否時は「回答を生成できませんでした」ではなく、拒否である旨を出典つきで返す。
    [Fact]
    public async Task AskAsync_WhenGatewayReportsRefusal_ReturnsRefusalMessage()
    {
        var orchestrator = new RagOrchestrator(
            new RoutingHttpClientFactory(CompletionJson(stopReason: "refusal", text: "")));

        var answer = await orchestrator.AskAsync("質問", "user-1", new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken);

        answer.Answer.Should().Contain("拒否");
        answer.Answer.Should().NotContain("回答を生成できませんでした");
    }

    // 正常終了（end_turn）は従来どおり本文をそのまま返す（回帰防止）。
    [Fact]
    public async Task AskAsync_WhenEndTurn_ReturnsBodyUnchanged()
    {
        var orchestrator = new RagOrchestrator(
            new RoutingHttpClientFactory(CompletionJson(stopReason: "end_turn", text: "回答本文")));

        var answer = await orchestrator.AskAsync("質問", "user-1", new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken);

        answer.Answer.Should().Be("回答本文");
    }

    // IADR-0037 の SSE 経路でも done イベントの stopReason で拒否を判別する。
    // 送出済みデルタは撤回できないため、拒否である旨を追記して呼び出し側が気づけるようにする。
    [Fact]
    public async Task AskStreamAsync_WhenDoneReportsRefusal_EmitsRefusalToken()
    {
        const string sse =
            "data: {\"delta\":\"\",\"done\":true,\"sent\":true,\"model\":\"claude-opus-5\"," +
            "\"inputTokens\":11,\"outputTokens\":0,\"stopReason\":\"refusal\"}\n\n";
        var orchestrator = new RagOrchestrator(
            new RoutingHttpClientFactory(sse, "text/event-stream"));

        var events = new List<AskEvent>();
        await foreach (var ev in orchestrator.AskStreamAsync("質問", "user-1", new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken))
            events.Add(ev);

        events.OfType<AskTokenEvent>().Should().Contain(t => t.Text.Contains("拒否"));
        events.Last().Should().BeOfType<AskDoneEvent>();
    }

    // 本文デルタが出ていない拒否（典型例）では、注記の前に空行を入れない。
    // フロントは token を 1 つの文字列へ連結し `white-space: pre-wrap` で表示するため、
    // 先頭に改行を入れると回答冒頭が空行になる。
    [Fact]
    public async Task AskStreamAsync_WhenRefusedWithoutDeltas_EmitsNoticeWithoutLeadingBlankLine()
    {
        const string sse =
            "data: {\"delta\":\"\",\"done\":true,\"sent\":true,\"model\":\"claude-opus-5\"," +
            "\"inputTokens\":11,\"outputTokens\":0,\"stopReason\":\"refusal\"}\n\n";
        var orchestrator = new RagOrchestrator(
            new RoutingHttpClientFactory(sse, "text/event-stream"));

        var events = new List<AskEvent>();
        await foreach (var ev in orchestrator.AskStreamAsync("質問", "user-1", new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken))
            events.Add(ev);

        var notice = events.OfType<AskTokenEvent>().Should().ContainSingle().Subject;
        notice.Text.Should().StartWith("（");
        notice.Text.Should().NotStartWith("\n");
    }

    // 部分本文が既に流れた後の拒否では、注記を空行で区切る。連結表示のため区切らないと
    // 「書きかけの本文（AI が回答の生成を拒否しました。）」と地の文へ溶け込んで読めなくなる。
    // 拒否は末尾の done で確定するため既出デルタは撤回できない（IADR-0104 §結果のトレードオフ）。
    [Fact]
    public async Task AskStreamAsync_WhenRefusedAfterDeltas_SeparatesNoticeFromBody()
    {
        const string sse =
            "data: {\"delta\":\"書きかけの本文\"}\n\n" +
            "data: {\"delta\":\"\",\"done\":true,\"sent\":true,\"model\":\"claude-opus-5\"," +
            "\"inputTokens\":11,\"outputTokens\":5,\"stopReason\":\"refusal\"}\n\n";
        var orchestrator = new RagOrchestrator(
            new RoutingHttpClientFactory(sse, "text/event-stream"));

        var events = new List<AskEvent>();
        await foreach (var ev in orchestrator.AskStreamAsync("質問", "user-1", new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken))
            events.Add(ev);

        var tokens = events.OfType<AskTokenEvent>().ToList();
        tokens.Should().HaveCount(2);
        tokens[0].Text.Should().Be("書きかけの本文");
        tokens[1].Text.Should().StartWith("\n\n");        // 地の文と分離して表示される
        tokens[1].Text.Should().Contain("拒否");
    }

    private static string CompletionJson(string stopReason, string text) =>
        $$"""
        {"text":"{{text}}","model":"claude-opus-5","inputTokens":11,"outputTokens":0,
         "sent":true,"endpoint":"claude-managed","routingReason":"ok","stopReason":"{{stopReason}}"}
        """;

    // 認可（許可）・検索（0 件）は固定応答を返し、LlmGateway だけテストごとの本文を返すスタブ。
    private sealed class RoutingHttpClientFactory(string llmBody, string llmMediaType = "application/json")
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var (body, mediaType) = name switch
            {
                "AuthorizationService" => ("""{"userId":"user-1","allowedFilters":[],"granted":true}""",
                    "application/json"),
                "RetrievalService" => ("""{"results":[],"total":0,"tookMs":0}""", "application/json"),
                _ => (llmBody, llmMediaType),
            };
            return new HttpClient(new FixedResponseHandler(body, mediaType))
            {
                BaseAddress = new Uri("http://localhost")
            };
        }
    }

    private sealed class FixedResponseHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }
}
