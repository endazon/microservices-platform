using System.Net;
using System.Text;
using AiAnalysisService.Domain.Ports;
using AiAnalysisService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Tests.Infrastructure.ExternalServices;

// FR-03, FR-04, FR-05, FR-07, NFR-09, NFR-16, UC-01, UC-02, ADR-0004, ADR-0029, ADR-0034 決定 1,
// ADR-0075, 計画 ADR-0086 決定 1, ADR-0087 決定 2, [[IADR-0009]], [[IADR-0379]] 決定 5,
// [[IADR-0400]], [[IADR-0415]], [[IADR-0426]] (#1255):
// RAG の検索が**輸送のポート越し**に走り、そこへ**利用者文脈と交差前の絞り込み**が
// 渡ることを固定する。
//
// 🔴 **輸送の中身ではなく「何を渡したか」を測る。** 渡す値が欠けると、
// 呼び出し先は「利用者が分からない」（`INVALID_ARGUMENT`）か「絞られていない」結果を返す ——
// 前者は縮退へ落ちて「文書が無い」に見え、後者は利用者の指定が無視される。
[Trait("TestKind", "Unit")]
public class RagOrchestratorSearchTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string DeniedText = "閲覧権限のある文書が見つかりませんでした。";

    private static AccessScopeResponse Granted() => new(
        "user-1", [new AttributeFilter("dept", ["sales", "hr"])], true);

    // 🔴 T-01: **検索の輸送は必須の依存である**（[[IADR-0533]] 決定 1。REST の並走は撤去済み）。
    // 既定値を持つと、差し込み忘れが黙って別の輸送へ落ちる（旧形は既定が REST だった）。
    [Fact]
    public void 検索の輸送は既定値を持たない必須の依存である()
    {
        typeof(RagOrchestrator).GetConstructors().Single()
            .GetParameters().Single(p => p.ParameterType == typeof(IRagSearchTransport))
            .HasDefaultValue.Should().BeFalse();
    }

    // 🔴 T-02 陽性対照: 質問経路は**利用者文脈と、利用者が指定した絞り込み（交差前）**を輸送へ渡す。
    [Fact]
    public async Task 質問経路は利用者文脈と交差前の絞り込みを輸送へ渡す()
    {
        var transport = new RecordingTransport();
        var orchestrator = Orchestrator(transport);

        await orchestrator.AskAsync("質問", "user-1",
            new Dictionary<string, string> { ["clearance"] = "internal" },
            new Dictionary<string, List<string>> { ["dept"] = ["sales"] }, Ct);

        var query = transport.Last;
        query.Should().NotBeNull();
        query!.UserId.Should().Be("user-1");
        query.UserAttributes.Should().Contain(
            new KeyValuePair<string, string>("clearance", "internal"));
        query.NarrowTo.Should().ContainKey("dept");
        query.NarrowTo!["dept"].Should().Equal(["sales"]);

        // ★ 陽性対照: 交差済みの実効スコープも**同時に**運ばれている
        //（交差済みなので `dept` は指定で絞られている）。
        query.EffectiveScope.GrantsAccess.Should().BeTrue();
        query.EffectiveScope.Filters.Single(f => f.Key == "dept").AllowedValues
            .Should().Equal(["sales"], "実効スコープは交差済みである");
    }

    // 🔴 T-03: 分析経路も**同じ形**で運ぶ（データ範囲の器から絞り込みを取り出す）。
    [Fact]
    public async Task 分析経路もデータ範囲の絞り込みを輸送へ渡す()
    {
        var transport = new RecordingTransport();
        var orchestrator = Orchestrator(transport);

        await orchestrator.AnalyzeAsync(
            new AnalysisTaskRequest("比較して", Range: new AnalysisDataRange(
                AttributeFilters: new Dictionary<string, List<string>> { ["dept"] = ["hr"] })),
            "user-1", new Dictionary<string, string> { ["clearance"] = "internal" }, Ct);

        transport.Last!.NarrowTo!["dept"].Should().Equal(["hr"]);
        transport.Last.UserId.Should().Be("user-1");
    }

    // 🔴 T-04 陰性対照: **輸送が空を返しても回答そのものは落ちない。**
    // 中立文言（[[IADR-0009]] 存在秘匿）へ倒れる —— 例外を伝播させると north-south の 500 になる。
    [Fact]
    public async Task 検索が空でも中立文言で返る()
    {
        var orchestrator = Orchestrator(new RecordingTransport());

        var answer = await orchestrator.AskAsync("質問", "user-1",
            new Dictionary<string, string>(), null, Ct);

        answer.Citations.Should().BeEmpty();
        answer.Answer.Should().NotBeNullOrWhiteSpace();
    }

    // 🔴 T-05 陰性対照: **権限が無ければ輸送を 1 度も呼ばない**（deny-by-default）。
    // 呼ぶと、呼び出し先に「権限外の問い合わせ」が届く。
    [Fact]
    public async Task 権限が無ければ検索を1度も呼ばない()
    {
        var transport = new RecordingTransport();
        var orchestrator = TestRagOrchestrator.Create(
            new StubHttpClientFactory(scopeJson: Json(new AccessScopeResponse("user-1", [], false))),
            search: transport);

        var answer = await orchestrator.AskAsync("質問", "user-1",
            new Dictionary<string, string>(), null, Ct);

        transport.Last.Should().BeNull();
        answer.Answer.Should().Be(DeniedText);
    }

    // ── 器 ────────────────────────────────────────────────────────

    private static RagOrchestrator Orchestrator(IRagSearchTransport transport) =>
        TestRagOrchestrator.Create(new StubHttpClientFactory(Json(Granted())), search: transport);

    private static string Json(AccessScopeResponse scope) =>
        System.Text.Json.JsonSerializer.Serialize(
            scope, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private sealed class RecordingTransport : IRagSearchTransport
    {
        public RagSearchQuery? Last { get; private set; }

        public Task<IReadOnlyList<SearchResultDto>> SearchAsync(
            RagSearchQuery query, CancellationToken ct)
        {
            Last = query;
            return Task.FromResult<IReadOnlyList<SearchResultDto>>([]);
        }
    }

    // `/authz/scope` は指定のスコープ、LLM ゲートウェイは非 2xx（出典のみの枝へ倒す）。
    private sealed class StubHttpClientFactory(string? scopeJson = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHandler(scopeJson)) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class StubHandler(string? scopeJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/authz/scope" && scopeJson is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(scopeJson, Encoding.UTF8, "application/json"),
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
