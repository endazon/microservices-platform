using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Bff.Tests;

// FR-18, UC-10, SC-21, ADR-0033, ADR-0034 (#918): AI 提案一覧の BFF 公開。
//
// 🔴 **「GraphService 側で効いているから BFF 経由でも効く」は測った証拠にならない**（#952 の教訓）。
// 認可と存在秘匿の境界を跨ぐので、BFF 層で改めて測る。
//
// 🔴 **本 issue が BFF へ開けるのは読み取り口だけである。** 承認・却下・生成は開けない
// （SC-03 の承認欄 = #452 と同じ PR で開ける）。**不在をルート表の走査で固定する。**
public class BffGraphSuggestionTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffGraphSuggestionTests(BffTestFactory factory) => _factory = factory;

    private static readonly Guid SourceId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TargetId = new("aaaaaaaa-0000-0000-0000-000000000002");

    private static AiSuggestionDto Link() => new(
        Guid.NewGuid(), "link", SourceId, TargetId, Guid.NewGuid(), null,
        "両文書が同じ規程を引いている", "pending", 0, null, "経費精算規程 v3.2", "旅費規程");

    private HttpClient CreateAuthenticatedClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        return c;
    }

    // C-01 陽性対照: 権限のある利用者で 200 が返り、資格情報が後段へ届いている。
    //
    // 🔴 **この陽性対照が無いと、変異（Authorization の伝播を外す）が検出できない。**
    // 伝播が切れると全部 401 になり、否定形（未認証 → 401）のテストは緑のままである。
    [Fact]
    public async Task Suggestions_are_returned_and_credentials_reach_the_backend()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubAiSuggestions = [Link()];

        var res = await CreateAuthenticatedClient()
            .GetAsync("/bff/graph/suggestions", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastGraphForwardedAuthorization.Should().NotBeNullOrEmpty(
            "Authorization を伝播しないと GraphService の RequireAuthorization が 401 で弾く");
        var items = await res.Content.ReadFromJsonAsync<List<AiSuggestionDto>>(
            TestContext.Current.CancellationToken);
        // SC-21 主要素 1: 一覧は両端の文書名を運ぶ（ID だけでは「提案の内容」列を描けない）。
        items!.Single().SourceDocumentTitle.Should().Be("経費精算規程 v3.2");
        items!.Single().TargetDocumentTitle.Should().Be("旅費規程");
    }

    // 🔴 後段のパスは**末尾スラッシュつき**である（群 /graph/suggestions の直下の MapGet("/")）。
    // 落とすと 404 になり、画面には「提案が無い」ではなく後段エラーとして出る。
    [Fact]
    public async Task It_calls_the_listing_path_with_the_trailing_slash()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubAiSuggestions = [];

        await CreateAuthenticatedClient()
            .GetAsync("/bff/graph/suggestions", TestContext.Current.CancellationToken);

        _factory.LastGraphPath.Should().Be("/graph/suggestions/");
    }

    // SC-21 入力/バリデーション: 状態・種類の絞りを**そのまま後段へ渡す**。
    // BFF は正規化も既定値の補完もしない（既定の情報源を 2 つにしない）。
    [Theory]
    [InlineData("?state=approved", "state=approved")]
    [InlineData("?state=all", "state=all")]
    [InlineData("?kind=tag", "kind=tag")]
    public async Task Filters_are_forwarded_verbatim(string query, string expected)
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubAiSuggestions = [];

        await CreateAuthenticatedClient()
            .GetAsync($"/bff/graph/suggestions{query}", TestContext.Current.CancellationToken);

        _factory.LastGraphPath.Should().Contain(expected);
    }

    // 陽性対照の対: **絞りを指定しなければクエリを足さない。**
    // BFF が既定（pending）を補うと、既定値が BFF と GraphService の 2 か所に生まれる。
    [Fact]
    public async Task No_default_filter_is_injected_by_the_bff()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubAiSuggestions = [];

        await CreateAuthenticatedClient()
            .GetAsync("/bff/graph/suggestions", TestContext.Current.CancellationToken);

        _factory.LastGraphPath.Should().NotContain("state=",
            "既定（pending）は GraphService が一箇所で持つ");
    }

    // C-03: 未認証は BFF の入口で 401。後段へ行かない。
    [Fact]
    public async Task Unauthenticated_is_401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.GetAsync("/bff/graph/suggestions",
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // C-04: 後段の 400（invalid_state / invalid_kind）はそのまま透過する。
    [Fact]
    public async Task Backend_400_passes_through()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.BadRequest;

        var res = await CreateAuthenticatedClient()
            .GetAsync("/bff/graph/suggestions?state=maybe", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // C-05: 後段へ到達できないときは 200 + 空配列へ**縮退しない**。
    // 「提案が 1 件も無い」と「一覧が引けない」は利用者にとって別の意味である。
    [Fact]
    public async Task Backend_error_is_not_degraded_to_an_empty_listing()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.InternalServerError;

        var res = await CreateAuthenticatedClient()
            .GetAsync("/bff/graph/suggestions", TestContext.Current.CancellationToken);

        res.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "空の一覧を返すと『棚卸しするものが無い』と読めてしまう");
    }

    // ルート衝突が無いこと。`/bff/graph/{documentId:guid}` は "suggestions" を GUID として
    // 解釈しないため、両者は別の口として並存する。**制約を外すと静かに衝突する。**
    [Fact]
    public async Task Suggestions_route_does_not_collide_with_the_node_route()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubAiSuggestions = [];
        _factory.StubGraphView = new GraphViewDto([new GraphNodeItemDto(Guid.NewGuid(), "A")], [], false);

        await CreateAuthenticatedClient()
            .GetAsync("/bff/graph/suggestions", TestContext.Current.CancellationToken);
        var listingPath = _factory.LastGraphPath;

        await CreateAuthenticatedClient()
            .GetAsync($"/bff/graph/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        var nodePath = _factory.LastGraphPath;

        listingPath.Should().Be("/graph/suggestions/");
        nodePath.Should().StartWith("/graph/").And.NotContain("suggestions");
    }

    // 🔴 FR-18・SC-21「描いてはいけないもの」: **一括承認の口も、1 件ずつの承認・却下の口も
    // BFF には無い。** 前者は禁止されており、後者は SC-03 の承認欄（#452）と同じ PR で開ける。
    //
    // **装置の検出力を先に確かめる** —— 提案の読み取り口が取れないなら、下の否定形は自明に成り立つ。
    [Fact]
    public void No_write_route_for_suggestions_is_exposed_by_the_bff()
    {
        using var scope = _factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<IEnumerable<EndpointDataSource>>();
        var endpoints = sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();

        endpoints.Should().NotBeEmpty("ルートが 1 本も取れないと本テストは空振りする");

        var suggestionRoutes = endpoints
            .Where(e => (e.RoutePattern.RawText ?? string.Empty)
                .Contains("suggestions", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 陽性対照: 読み取り口は在る。
        suggestionRoutes.Should().ContainSingle(
            "提案の口が 1 本も無いなら、下の否定形は自明に成り立ってしまう（装置の検出力）");
        suggestionRoutes.Single().RoutePattern.RawText.Should().Be("/bff/graph/suggestions");

        var methods = suggestionRoutes
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            .Distinct()
            .ToList();

        methods.Should().BeEquivalentTo(["GET"],
            "SC-21 は書き込みを一切しない画面である。"
            + "承認・却下は SC-03 経由でのみ実行され、一括承認はどの層にも作らない");
    }
}
