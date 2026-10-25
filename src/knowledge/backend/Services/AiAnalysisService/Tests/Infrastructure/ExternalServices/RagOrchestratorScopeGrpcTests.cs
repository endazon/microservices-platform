using System.Net;
using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-05, UC-01, UC-02, NFR-09, NFR-16, ADR-0004, ADR-0029, ADR-0034, ADR-0075,
// [[IADR-0009]], [[IADR-0401]] 決定 1, [[IADR-0533]] 決定 1 (#1255):
// **T-P2-01** —— ABAC スコープ解決の gRPC 経路が、許可・不許可・失敗のそれぞれで正しい分岐へ進むことを固定する。
// （旧形は REST 経路との同値を測っていた。[[IADR-0533]] で REST 経路を撤去したので、分岐そのものを表明する。）
//
// 🔴 **観測点は「解決したスコープ」そのものではなく、そこから分かれる応答である。**
// `ResolveScopeAsync` は private であり、外から見えるのは
//   許可 → 出典イベント → LLM の枝／不許可 → 中立文言（[[IADR-0009]] 存在秘匿）
// という**分岐の側**である。
[Trait("TestKind", "Unit")]
public class RagOrchestratorScopeGrpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string DeniedText = "閲覧権限のある文書が見つかりませんでした。";

    private static AccessScopeResponse Granted() => new(
        "user-1",
        [new AttributeFilter("confidentiality", ["internal", "public"])],
        true,
        [new AccessScopeBranch("attribute", [new AttributeFilter("confidentiality", ["internal", "public"])])]);

    private static AccessScopeResponse Denied() => new("user-1", [], false);

    private static async Task<List<AskEvent>> StreamAsync(RagOrchestrator orchestrator)
    {
        var events = new List<AskEvent>();
        await foreach (var ev in orchestrator.AskStreamAsync(
            "質問", "user-1", new Dictionary<string, string> { ["clearance"] = "internal" }, ct: Ct))
        {
            events.Add(ev);
        }
        return events;
    }

    // gRPC 経路の器（スコープは偽の生成クライアント。検索は 0 件、生成は不達）。
    private static RagOrchestrator GrpcOrchestrator(FakeAuthzScopeClient fake) =>
        TestRagOrchestrator.Create(new RoutingHttpClientFactory(), authz: fake.Wrap());

    // T-P2-01（陽性）: 許可されたスコープでは、出典イベントを出して LLM の枝へ進む。
    [Fact]
    public async Task Grpc_takes_the_llm_branch_when_granted()
    {
        var fake = FakeAuthzScopeClient.Returning(Granted());
        var events = await StreamAsync(GrpcOrchestrator(fake));

        events.Should().Contain(e => e is AskCitationsEvent);
        events.OfType<AskTokenEvent>().Select(t => t.Text).Should().NotContain(DeniedText,
            "許可されているのだから存在秘匿の中立文言にはならない");
        events.Last().Should().BeOfType<AskDoneEvent>();
        fake.CallCount.Should().Be(1, "陽性対照: 実際に gRPC を通っている");
        fake.LastRequest!.Action.Should().Be("read");
    }

    // T-P2-01（陰性）: 不許可では中立文言で縮退する（[[IADR-0009]] 存在秘匿）。
    [Fact]
    public async Task Grpc_takes_the_denied_branch_when_denied()
    {
        var events = await StreamAsync(GrpcOrchestrator(FakeAuthzScopeClient.Returning(Denied())));

        events.OfType<AskTokenEvent>().Should().ContainSingle().Which.Text.Should().Be(DeniedText);
    }

    // 縮退: gRPC の輸送失敗はすべて deny-by-default（不許可と同じ枝）。
    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    public async Task Grpc_failure_degrades_to_the_denied_branch(StatusCode status)
    {
        var events = await StreamAsync(GrpcOrchestrator(FakeAuthzScopeClient.Failing(status)));

        events.OfType<AskTokenEvent>().Should().ContainSingle().Which.Text.Should().Be(DeniedText);
        events.Last().Should().BeOfType<AskDoneEvent>();
    }

    // 非ストリーミング（`AskAsync`）でも、許可は LLM の枝・不許可は中立文言へ分かれること。
    [Fact]
    public async Task AskAsync_takes_the_same_branches()
    {
        var granted = await GrpcOrchestrator(FakeAuthzScopeClient.Returning(Granted())).AskAsync(
            "質問", "user-1", new Dictionary<string, string>(), ct: Ct);
        var denied = await GrpcOrchestrator(FakeAuthzScopeClient.Returning(Denied())).AskAsync(
            "質問", "user-1", new Dictionary<string, string>(), ct: Ct);

        granted.Answer.Should().NotBe(DeniedText);
        denied.Answer.Should().Be(DeniedText);
        denied.Citations.Should().BeEmpty();
    }

    // 検索と生成の応答の器。`/authz/scope` を呼ばれること自体が誤り（スコープは gRPC の偽物から来る）なので落とす。
    private sealed class RoutingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new RoutingHandler()) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/authz/scope")
                throw new InvalidOperationException("スコープは gRPC の偽物から来る。/authz/scope を呼んではならない");

            if (path == "/search")
                return Task.FromResult(Json("""{"results":[],"total":0,"tookMs":0}"""));

            // LLM ゲートウェイは**非 2xx** を返す（gRPC 輸送では UNAVAILABLE へ写る）。
            // 本テストの関心はスコープ解決の分岐なので、生成側は縮退の枝に固定する。
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
    }
}
