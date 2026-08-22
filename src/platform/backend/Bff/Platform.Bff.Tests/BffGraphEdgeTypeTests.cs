using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace Platform.Bff.Tests;

// FR-17, ADR-0033, ADR-0039 (#962): 辺の型カタログの BFF 公開。
//
// **覆うのは FR-17 である。** 消費者となる画面は未実装なので画面の ID を参照しない。
//
// 🔴 **「GraphService 側で効いているから BFF 経由でも効く」は測った証拠にならない**（#952 の教訓）。
// 認可の境界を跨ぐので BFF 層で改めて測る。
public class BffGraphEdgeTypeTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffGraphEdgeTypeTests(BffTestFactory factory) => _factory = factory;

    private HttpClient CreateAuthenticatedClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        return c;
    }

    // C-01 陽性対照: 権限のある利用者で 200 が返り、資格情報が後段へ届いている。
    //
    // 🔴 **この陽性対照が無いと、変異 E-1（Authorization の伝播を外す）が検出できない。**
    // 伝播が切れると全部 401 になり、否定形（未認証 → 401）のテストは緑のままである。
    [Fact]
    public async Task Edge_type_catalog_is_returned_and_credentials_reach_the_backend()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubEdgeTypeCatalog =
        [
            new EdgeTypeCatalogItemDto(Guid.NewGuid(), "references", "core", false),
            new EdgeTypeCatalogItemDto(Guid.NewGuid(), "related-to", "core", true),
        ];

        var res = await CreateAuthenticatedClient()
            .GetAsync("/bff/graph/edge-types", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastGraphForwardedAuthorization.Should().NotBeNullOrEmpty(
            "Authorization を伝播しないと GraphService の RequireAuthorization が 401 で弾く");
        var items = await res.Content.ReadFromJsonAsync<List<EdgeTypeCatalogItemDto>>(
            TestContext.Current.CancellationToken);
        items!.Select(i => i.Name).Should().BeEquivalentTo(["references", "related-to"]);
    }

    // 🔴 後段は使用件数つきの管理者向け口ではなく、カタログ（認証のみ・件数なし）である。
    //
    // 向き先を `/graph/edge-types` にすると (1) 一般利用者が 403 になり
    // (2) ABAC で絞られていない集計値が漏れる。**経路を固定する。**
    [Fact]
    public async Task It_calls_the_catalog_path_not_the_admin_listing()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubEdgeTypeCatalog = [];

        await CreateAuthenticatedClient()
            .GetAsync("/bff/graph/edge-types", TestContext.Current.CancellationToken);

        _factory.LastGraphPath.Should().Be("/graph/edge-types/catalog",
            "管理者向けの /graph/edge-types へ向けると 403 になり、かつ使用件数が漏れる");
    }

    // C-04: 後段の 403 はそのまま透過する。BFF が握り潰さない。
    [Fact]
    public async Task Backend_403_passes_through()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.Forbidden;

        var res = await CreateAuthenticatedClient()
            .GetAsync("/bff/graph/edge-types", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "200 + 空配列へ潰すと、利用者は権限不足を知れない");
    }

    // C-05: 後段へ到達できないときは 502。**空配列へ縮退しない。**
    // 「型が 1 つも無い」と「辞書が引けない」は利用者にとって別の意味である。
    [Fact]
    public async Task Backend_error_is_not_degraded_to_an_empty_catalog()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.InternalServerError;

        var res = await CreateAuthenticatedClient()
            .GetAsync("/bff/graph/edge-types", TestContext.Current.CancellationToken);

        res.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "空の辞書を返すと『辺の型が定義されていない』と読めてしまう");
    }

    // C-03: 未認証は BFF の入口で 401。後段へ行かない。
    [Fact]
    public async Task Unauthenticated_is_401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.GetAsync("/bff/graph/edge-types", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ルート衝突が無いこと。`/bff/graph/{documentId:guid}` は "edge-types" を GUID として
    // 解釈しないため、両者は別の口として並存する。**制約を外すと静かに衝突する。**
    [Fact]
    public async Task Edge_types_route_does_not_collide_with_the_node_route()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubEdgeTypeCatalog = [];
        _factory.StubGraphView = new GraphViewDto([new GraphNodeItemDto(Guid.NewGuid(), "A")], [], false);

        await CreateAuthenticatedClient()
            .GetAsync("/bff/graph/edge-types", TestContext.Current.CancellationToken);
        var catalogPath = _factory.LastGraphPath;

        await CreateAuthenticatedClient()
            .GetAsync($"/bff/graph/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        var nodePath = _factory.LastGraphPath;

        catalogPath.Should().Be("/graph/edge-types/catalog");
        nodePath.Should().StartWith("/graph/").And.NotContain("edge-types");
    }
}
