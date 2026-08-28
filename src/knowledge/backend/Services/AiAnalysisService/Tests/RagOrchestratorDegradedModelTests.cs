using System.Net;
using System.Text;
using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using AwesomeAssertions;

namespace AiAnalysisService.Tests;

// FR-04, FR-05, FR-11, IADR-0111 (#403): 応答が名乗る「使用モデル」が実際に使われたモデルと一致することを固定する。
// 縮退経路（ABAC 不許可・越境拒否・呼び出し失敗）は LLM を呼んでいないため、モデル名を捏造してはならない。
// 以前は存在しない設定キー `Llm:DefaultModel` のフォールバックで常に "claude-opus-5" を名乗っていた。
public class RagOrchestratorDegradedModelTests
{
    // 用途 rag-answer の実 route 結果（ADR-0022 / IADR-0106）。ゲートウェイが解決して報告する値。
    private const string ResolvedModel = "claude-sonnet-5";

    // T-10: ABAC で閲覧可能文書が無い縮退。ゲートウェイを一度も呼んでいないためモデルは未使用（空）。
    [Fact]
    public async Task AskAsync_WhenAbacDenied_ReportsNoModel()
    {
        var orchestrator = Create(new StubHttpClientFactory(granted: false));

        var answer = await orchestrator.AskAsync("質問", "user-1", new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken);

        answer.Model.Should().BeEmpty();
        answer.Model.Should().NotBe("claude-opus-5");
    }

    // T-11: ストリーミング版の同経路。done イベントも同じく未使用（空）を名乗る。
    [Fact]
    public async Task AskStreamAsync_WhenAbacDenied_DoneReportsNoModel()
    {
        var orchestrator = Create(new StubHttpClientFactory(granted: false));

        var done = await LastDoneAsync(orchestrator);

        done.Model.Should().BeEmpty();
    }

    // T-12: ゲートウェイが機密区分により送信拒否（sent=false・model は空）。外部送信していないため空を透過する。
    [Fact]
    public async Task AskAsync_WhenGatewayDeniesEgress_ReportsNoModel()
    {
        var orchestrator = Create(new StubHttpClientFactory(
            llmBody: CompletionJson(sent: false, model: "", text: "機密区分により送信できません。")));

        var answer = await orchestrator.AskAsync("質問", "user-1", new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken);

        answer.Model.Should().BeEmpty();
    }

    // T-12（ストリーミング）: SSE の done も sent=false・model 空で届くため、そのまま透過する。
    [Fact]
    public async Task AskStreamAsync_WhenGatewayDeniesEgress_DoneReportsNoModel()
    {
        var orchestrator = Create(new StubHttpClientFactory(
            llmBody: "data: {\"delta\":\"\",\"done\":true,\"sent\":false," +
                     "\"text\":\"機密区分により送信できません。\",\"model\":\"\"}\n\n",
            llmMediaType: "text/event-stream"));

        var done = await LastDoneAsync(orchestrator);

        done.Model.Should().BeEmpty();
    }

    // T-13: ゲートウェイへ到達できない（非 2xx）。モデルは解決されていないため空。
    [Fact]
    public async Task AskAsync_WhenGatewayHttpFails_ReportsNoModel()
    {
        var orchestrator = Create(new StubHttpClientFactory(
            llmBody: "unavailable", llmStatus: HttpStatusCode.ServiceUnavailable));

        var answer = await orchestrator.AskAsync("質問", "user-1", new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken);

        answer.Model.Should().BeEmpty();
    }

    // T-13（ストリーミング）: 送信自体が失敗した場合も同様（下位ヘルパが done(Sent=false) へ縮退させる）。
    [Fact]
    public async Task AskStreamAsync_WhenGatewayHttpFails_DoneReportsNoModel()
    {
        var orchestrator = Create(new StubHttpClientFactory(
            llmBody: "unavailable", llmStatus: HttpStatusCode.ServiceUnavailable));

        var done = await LastDoneAsync(orchestrator);

        done.Model.Should().BeEmpty();
    }

    // T-14: 正常に送信できた場合は従来どおり実モデル名（route 結果）を返す（回帰防止）。
    [Fact]
    public async Task AskAsync_WhenSent_ReportsResolvedModel()
    {
        var orchestrator = Create(new StubHttpClientFactory(
            llmBody: CompletionJson(sent: true, model: ResolvedModel, text: "回答本文")));

        var answer = await orchestrator.AskAsync("質問", "user-1", new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken);

        answer.Model.Should().Be(ResolvedModel);
    }

    // T-14（ストリーミング）: done の model をそのまま載せる（回帰防止）。
    [Fact]
    public async Task AskStreamAsync_WhenSent_DoneReportsResolvedModel()
    {
        var orchestrator = Create(new StubHttpClientFactory(
            llmBody: "data: {\"delta\":\"回答\"}\n\n" +
                     $"data: {{\"delta\":\"\",\"done\":true,\"sent\":true,\"model\":\"{ResolvedModel}\"," +
                     "\"inputTokens\":11,\"outputTokens\":7}\n\n",
            llmMediaType: "text/event-stream"));

        var done = await LastDoneAsync(orchestrator);

        done.Model.Should().Be(ResolvedModel);
    }

    // T-15: 呼び出し先が不調（sent=false だがゲートウェイは route 済みで呼び出しを試みた）。
    // どのモデルへ向けた試行かは監査・障害解析の情報になるため、空へ潰さず透過する。
    [Fact]
    public async Task AskAsync_WhenUpstreamFailed_KeepsResolvedModel()
    {
        var orchestrator = Create(new StubHttpClientFactory(
            llmBody: CompletionJson(sent: false, model: ResolvedModel,
                text: "呼び出し先 claude-managed が現在利用できません。")));

        var answer = await orchestrator.AskAsync("質問", "user-1", new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken);

        answer.Model.Should().Be(ResolvedModel);
    }

    // T-15（ストリーミング）: SSE の縮退 done も model を持つ場合はそのまま透過する。
    [Fact]
    public async Task AskStreamAsync_WhenUpstreamFailed_KeepsResolvedModel()
    {
        var orchestrator = Create(new StubHttpClientFactory(
            llmBody: "data: {\"delta\":\"\",\"done\":true,\"sent\":false," +
                     $"\"text\":\"呼び出し先が現在利用できません。\",\"model\":\"{ResolvedModel}\"}}\n\n",
            llmMediaType: "text/event-stream"));

        var done = await LastDoneAsync(orchestrator);

        done.Model.Should().Be(ResolvedModel);
    }

    // T-16: 2xx だが本文が JSON の null（＝逆シリアル化結果が null）。モデルは解決されていないため空。
    // ModelOrNone が null を応答契約へ載せないことの回帰固定でもある。
    [Fact]
    public async Task AskAsync_WhenGatewayBodyIsNull_ReportsNoModel()
    {
        var orchestrator = Create(new StubHttpClientFactory(llmBody: "null"));

        var answer = await orchestrator.AskAsync("質問", "user-1", new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken);

        answer.Model.Should().BeEmpty();
        answer.Answer.Should().Be("回答を生成できませんでした。");
    }

    private static RagOrchestrator Create(IHttpClientFactory factory) => new(factory);

    private static async Task<AskDoneEvent> LastDoneAsync(IRagOrchestrator orchestrator)
    {
        var events = new List<AskEvent>();
        await foreach (var ev in orchestrator.AskStreamAsync("質問", "user-1", new Dictionary<string, string>()))
            events.Add(ev);
        return events.OfType<AskDoneEvent>().Should().ContainSingle().Subject;
    }

    private static string CompletionJson(bool sent, string model, string text) =>
        $$"""
        {"text":"{{text}}","model":"{{model}}","inputTokens":0,"outputTokens":0,
         "sent":{{(sent ? "true" : "false")}},"endpoint":"claude-managed","routingReason":"test"}
        """;

    // 認可（granted 可変）・検索（0 件）は固定応答、LlmGateway だけテストごとの本文・ステータスを返すスタブ。
    private sealed class StubHttpClientFactory(
        string llmBody = "{}",
        string llmMediaType = "application/json",
        HttpStatusCode llmStatus = HttpStatusCode.OK,
        bool granted = true) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var (body, mediaType, status) = name switch
            {
                "AuthorizationService" => (
                    $$"""{"userId":"user-1","allowedFilters":[],"granted":{{(granted ? "true" : "false")}}}""",
                    "application/json", HttpStatusCode.OK),
                "RetrievalService" => ("""{"results":[],"total":0,"tookMs":0}""",
                    "application/json", HttpStatusCode.OK),
                _ => (llmBody, llmMediaType, llmStatus),
            };
            return new HttpClient(new FixedResponseHandler(body, mediaType, status))
            {
                BaseAddress = new Uri("http://localhost")
            };
        }
    }

    private sealed class FixedResponseHandler(string body, string mediaType, HttpStatusCode status)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }
}
