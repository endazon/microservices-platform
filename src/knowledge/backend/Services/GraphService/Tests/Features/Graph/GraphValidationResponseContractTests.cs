using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Features.AiSuggestions.List;
using GraphService.Features.EdgeTypes.Create;
using GraphService.Features.EdgeTypes.Rename;
using GraphService.Features.Graph.CreateEdge;
using GraphService.Features.Graph.Neighbors;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests.Features.Graph;

// FR-17, FR-18, UC-10, SC-09, SC-18, SC-21 / IADR-0371 決定 2 / IADR-0395:
// 検証を FluentValidation へ移した際、**HTTP の面で応答が変わっていない**ことを固定する。
//
// 🔴 **既存の 400 の試験は状態コードしか見ていないものが多い。** 400 のままメッセージだけが
// 変わる退行（あるいは検証器の規則順が入れ替わって別の理由が返る退行）は、そこでは捕まらない。
// 本クラスは**本文**を端点越しに固定する。
//
// 🔴 **判定の順序も固定する** —— 検証が認可より前にあること（存在秘匿）と、改名だけは
// 404 が 400 より前にあることの 2 点である。
[Trait("TestKind", "Integration")]
public class GraphValidationResponseContractTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public GraphValidationResponseContractTests(TestWebApplicationFactory factory)
        => _factory = factory;

    // 何も見えないスコープ。**検証が認可より前にあるなら、このスコープでも 400 が返る。**
    private static AccessScopeResponse Denied() => new("test-user", [], false);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage resp)
        => await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    // ---- POST /graph/edges（1 欄の本文） ----

    [Fact]
    public async Task CreateEdge_EmptyDocumentId_Returns400WithOriginalBody()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync("/graph/edges",
            new CreateGraphEdgeRequest(Guid.Empty, Guid.NewGuid(), Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyOf(resp)).GetProperty("error").GetString()
            .Should().Be(CreateGraphEdgeValidator.DocumentIdRequiredMessage);
    }

    [Fact]
    public async Task CreateEdge_SelfEdge_Returns400WithOriginalBody()
    {
        var id = Guid.NewGuid();

        var resp = await _factory.CreateClient().PostAsJsonAsync("/graph/edges",
            new CreateGraphEdgeRequest(id, id, Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyOf(resp)).GetProperty("error").GetString()
            .Should().Be(CreateGraphEdgeValidator.SelfEdgeNotAllowedMessage);
    }

    // ---- GET /graph/{id}/neighbors（2 欄の本文 ＋ 認可より前） ----

    // 🔴 **`error` と `message` の 2 欄をどちらも見る。** 片方だけ見ると、機械語だけ・
    // 説明文だけが変わる退行が捕まらない。
    [Fact]
    public async Task Neighbors_HopsOutOfRange_Returns400WithBothFields()
    {
        _factory.ScopeProvider = _ => Denied();

        var resp = await _factory.CreateClient().GetAsync(
            $"/graph/{Guid.NewGuid()}/neighbors?hops={GraphTraversal.MaxHops + 1}",
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "hops の検証は認可より前である（後ろへ動かすと 404 になり、文書の存在が漏れる）");
        var body = await BodyOf(resp);
        body.GetProperty("error").GetString().Should().Be(NeighborsQueryValidator.HopsOutOfRangeCode);
        body.GetProperty("message").GetString().Should().Be(NeighborsQueryValidator.HopsOutOfRangeMessage);
    }

    [Fact]
    public async Task Neighbors_InvalidTypes_Returns400WithBothFields()
    {
        _factory.ScopeProvider = _ => Denied();

        var resp = await _factory.CreateClient().GetAsync(
            $"/graph/{Guid.NewGuid()}/neighbors?types=not-a-guid",
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "types の検証も認可より前である（hops と同じ理由）");
        var body = await BodyOf(resp);
        body.GetProperty("error").GetString()
            .Should().Be(NeighborsQueryValidator.EdgeTypeFilterInvalidCode);
        body.GetProperty("message").GetString()
            .Should().Be(NeighborsQueryValidator.EdgeTypeFilterInvalidMessage);
    }

    // 🔴 区切り文字だけの `types` は 400 ではない（絞らないだけ）。移送前と同じ縮退である。
    [Fact]
    public async Task Neighbors_TypesWithOnlySeparators_IsNotBadRequest()
    {
        _factory.ScopeProvider = _ => Denied();

        var resp = await _factory.CreateClient().GetAsync(
            $"/graph/{Guid.NewGuid()}/neighbors?types=,,,",
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "要素が 1 つも無い types は「絞らない」であり、検証を通って認可へ進む");
    }

    // ---- POST/PUT /graph/edge-types（1 欄の本文 ＋ 改名は 404 が先） ----

    [Fact]
    public async Task CreateEdgeType_EmptyName_Returns400WithOriginalBody()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync("/graph/edge-types",
            new CreateEdgeTypeRequest("   ", EdgeTypeLayer.Core, false),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyOf(resp)).GetProperty("error").GetString()
            .Should().Be(CreateEdgeTypeValidator.NameRequiredMessage);
    }

    [Fact]
    public async Task CreateEdgeType_InvalidLayer_Returns400WithOriginalBody()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync("/graph/edge-types",
            new CreateEdgeTypeRequest($"x-{Guid.NewGuid():N}", "misc", false),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyOf(resp)).GetProperty("error").GetString()
            .Should().Be(CreateEdgeTypeValidator.InvalidLayerMessage);
    }

    // 🔴 **改名は 404 が 400 より先である。** 検証をハンドラ先頭へ上げると、
    // 不存在の型 ID への空名改名が 400 に化ける。ここで止まる。
    [Fact]
    public async Task RenameEdgeType_UnknownIdWithEmptyName_Is404NotBadRequest()
    {
        var resp = await _factory.CreateClient().PutAsJsonAsync(
            $"/graph/edge-types/{Guid.NewGuid()}",
            new RenameEdgeTypeRequest("   "),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "検証を先頭へ上げると 404 が 400 に化ける（移送は振る舞いを変えない）");
    }

    [Fact]
    public async Task RenameEdgeType_ExistingIdWithEmptyName_Returns400WithOriginalBody()
    {
        var type = EdgeType.Create($"r-{Guid.NewGuid():N}", EdgeTypeLayer.Core, false);
        await _factory.SeedAsync(db =>
        {
            db.EdgeTypes.Add(type);
            return Task.CompletedTask;
        });

        var resp = await _factory.CreateClient().PutAsJsonAsync($"/graph/edge-types/{type.Id}",
            new RenameEdgeTypeRequest("   "), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyOf(resp)).GetProperty("error").GetString()
            .Should().Be(RenameEdgeTypeValidator.NameRequiredMessage);
    }

    // ---- GET /graph/suggestions（1 欄の本文 ＋ 認可より前） ----

    [Fact]
    public async Task ListAiSuggestions_InvalidState_Returns400WithOriginalBody()
    {
        _factory.ScopeProvider = _ => Denied();

        var resp = await _factory.CreateClient().GetAsync(
            "/graph/suggestions?state=unknown", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "検証は認可より前である（後ろへ動かすと 200 + [] になり、見える提案の有無が漏れる）");
        (await BodyOf(resp)).GetProperty("error").GetString()
            .Should().Be(ListAiSuggestionsQueryValidator.InvalidStateMessage);
    }

    [Fact]
    public async Task ListAiSuggestions_InvalidKind_Returns400WithOriginalBody()
    {
        _factory.ScopeProvider = _ => Denied();

        var resp = await _factory.CreateClient().GetAsync(
            "/graph/suggestions?kind=unknown", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyOf(resp)).GetProperty("error").GetString()
            .Should().Be(ListAiSuggestionsQueryValidator.InvalidKindMessage);
    }

    // 複数違反しても、返るのは**最初の規則**の本文である（移送前のガード節と同じ）。
    [Fact]
    public async Task ListAiSuggestions_BothInvalid_ReturnsFirstRuleBody()
    {
        _factory.ScopeProvider = _ => Denied();

        var resp = await _factory.CreateClient().GetAsync(
            "/graph/suggestions?state=unknown&kind=unknown", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyOf(resp)).GetProperty("error").GetString()
            .Should().Be(ListAiSuggestionsQueryValidator.InvalidStateMessage);
    }
}
