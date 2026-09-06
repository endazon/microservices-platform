using System.Net;
using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Grpc.Core;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-05, UC-01, UC-02, NFR-09, NFR-16, ADR-0004, ADR-0029, ADR-0034, ADR-0075,
// [[IADR-0009]], [[IADR-0379]] 決定 5, [[IADR-0401]] 決定 1 (#1255):
// **T-P2-01** —— ABAC スコープ解決の REST 経路と gRPC 経路が同じ答えを返すことを固定する。
//
// 🔴 **観測点は「解決したスコープ」そのものではなく、そこから分かれる応答である。**
// `ResolveScopeAsync` は private であり、外から見えるのは
//   許可 → 出典イベント → LLM の枝／不許可 → 中立文言（[[IADR-0009]] 存在秘匿）
// という**分岐の側**である。分岐が一致することが「同じスコープを得た」ことの証拠になる。
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

    private static string Json(AccessScopeResponse scope) =>
        System.Text.Json.JsonSerializer.Serialize(
            scope, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

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

    // REST 経路の器（`/authz/scope` は指定のスコープ、`/search` は 0 件、`/complete/stream` は不達）。
    private static RagOrchestrator RestOrchestrator(AccessScopeResponse scope) =>
        new(new RoutingHttpClientFactory(Json(scope)));

    // gRPC 経路の器（スコープだけ gRPC。以降は REST 経路と**同じ器**を使う）。
    private static RagOrchestrator GrpcOrchestrator(FakeAuthzScopeClient fake) =>
        new(new RoutingHttpClientFactory(scopeJson: null), authzScopeGrpc: fake.Wrap());

    // T-P2-01（陽性）: 許可されたスコープでは、両経路とも出典イベントを出して LLM の枝へ進む。
    [Fact]
    public async Task Rest_and_grpc_take_the_same_branch_when_granted()
    {
        var rest = await StreamAsync(RestOrchestrator(Granted()));

        var fake = FakeAuthzScopeClient.Returning(Granted());
        var grpc = await StreamAsync(GrpcOrchestrator(fake));

        grpc.Select(e => e.GetType()).Should().Equal(rest.Select(e => e.GetType()));
        grpc.OfType<AskTokenEvent>().Select(t => t.Text)
            .Should().Equal(rest.OfType<AskTokenEvent>().Select(t => t.Text));
        grpc.OfType<AskTokenEvent>().Select(t => t.Text).Should().NotContain(DeniedText,
            "許可されているのだから存在秘匿の中立文言にはならない");
        fake.CallCount.Should().Be(1, "陽性対照: 実際に gRPC を通っている");
        fake.LastRequest!.Action.Should().Be("read");
    }

    // T-P2-01（陰性）: 不許可では、両経路とも中立文言で縮退する（[[IADR-0009]] 存在秘匿）。
    [Fact]
    public async Task Rest_and_grpc_take_the_same_branch_when_denied()
    {
        var rest = await StreamAsync(RestOrchestrator(Denied()));
        var grpc = await StreamAsync(GrpcOrchestrator(FakeAuthzScopeClient.Returning(Denied())));

        grpc.Select(e => e.GetType()).Should().Equal(rest.Select(e => e.GetType()));
        grpc.OfType<AskTokenEvent>().Should().ContainSingle().Which.Text.Should().Be(DeniedText);
    }

    // 縮退: gRPC の輸送失敗はすべて deny-by-default（REST の非 2xx・不達と同じ枝）。
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

    // 非ストリーミング（`AskAsync`）でも同じ分岐であること。
    [Fact]
    public async Task AskAsync_agrees_between_rest_and_grpc()
    {
        var rest = await RestOrchestrator(Granted()).AskAsync(
            "質問", "user-1", new Dictionary<string, string>(), ct: Ct);
        var grpc = await GrpcOrchestrator(FakeAuthzScopeClient.Returning(Granted())).AskAsync(
            "質問", "user-1", new Dictionary<string, string>(), ct: Ct);

        grpc.Citations.Should().BeEquivalentTo(rest.Citations);
        grpc.Answer.Should().Be(rest.Answer);
    }

    // 経路ごとに応答を出し分ける器。`scopeJson` が null のときは `/authz/scope` を呼ばれること自体が
    // 誤り（gRPC 経路のはず）なので落とす。
    private sealed class RoutingHttpClientFactory(string? scopeJson) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new RoutingHandler(scopeJson)) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class RoutingHandler(string? scopeJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/authz/scope")
            {
                if (scopeJson is null)
                    throw new InvalidOperationException("gRPC 経路では /authz/scope を呼んではならない");
                return Task.FromResult(Json(scopeJson));
            }

            if (path == "/search")
                return Task.FromResult(Json("""{"results":[],"total":0,"tookMs":0}"""));

            // LLM ゲートウェイは**非 2xx** を返す（不達＝例外にはしない）。
            // 🔴 [[IADR-0400]] 決定 5: `GenerateAsync` の REST 実装は「非 2xx → 出典のみ」と
            // 「接続失敗 → 例外が伝播」を**別の枝**として持つ。本テストの関心はスコープ解決の分岐
            // なので、生成側は両経路が確実に同じ枝（出典のみ）へ落ちる非 2xx に固定する。
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
    }
}
