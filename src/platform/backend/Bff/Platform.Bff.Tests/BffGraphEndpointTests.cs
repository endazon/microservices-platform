using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace Platform.Bff.Tests;

// FR-17, UC-10, ADR-0034, ADR-0043: グラフ読み取りの BFF 公開（#916a）。
//
// 🔴 **「GraphService 側で効いているから BFF 経由でも効く」は測った証拠にならない。**
// 認可の境界を跨ぐので、BFF 層で改めて測る。
//
// スタブは Authorization の有無で応答を変える（無ければ 404）。GraphService が
// 自分で JWT から解決し、利用者を特定できなければ deny-closed へ倒れる型だからである。
public class BffGraphEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffGraphEndpointTests(BffTestFactory factory) => _factory = factory;

    // 既存の BFF テストと同じ作法: BFF 自体の認証は TestAuthHandler が通し、
    // **後段へ伝播されるのは Authorization ヘッダ**である。両者は別物なので明示的に付ける。
    private HttpClient CreateAuthenticatedClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        return c;
    }

    // B-01 陽性対照: 権限のある利用者では 200 が返り、資格情報が後段へ届いている。
    //
    // 🔴 **この陽性対照が本クラスで最も重要である。** 否定形（404 になること）だけを並べると、
    // **ヘッダ伝播を外す変異でも全部 404 のまま緑になる** —— 「常に 404 を返す実装」が
    // 変異試験を通ってしまう。
    [Fact]
    public async Task Authorized_user_gets_the_graph_and_credentials_reach_the_backend()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubGraphView = new GraphViewDto(
            [new GraphNodeItemDto(Guid.NewGuid(), "A")], [], false);

        var res = await CreateAuthenticatedClient()
            .GetAsync($"/bff/graph/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastGraphForwardedAuthorization.Should().NotBeNullOrEmpty(
            "Authorization を伝播しないと GraphService は anonymous として deny-closed へ倒れる");
        var view = await res.Content.ReadFromJsonAsync<GraphViewDto>(TestContext.Current.CancellationToken);
        view!.Nodes.Should().HaveCount(1);
    }

    // B-02 / B-03: 権限外と不存在はいずれも 404 で、**本文まで区別できない**。
    // GraphService の存在秘匿（ADR-0034 決定 2）が BFF 層でも保たれることを固定する。
    [Fact]
    public async Task Forbidden_and_nonexistent_are_both_404_and_indistinguishable()
    {
        var client = CreateAuthenticatedClient();

        _factory.GraphStubStatusCode = HttpStatusCode.NotFound;
        var forbidden = await client.GetAsync($"/bff/graph/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        var forbiddenBody = await forbidden.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var nonexistent = await client.GetAsync($"/bff/graph/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        var nonexistentBody = await nonexistent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        forbidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        nonexistent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        forbiddenBody.Should().Be(nonexistentBody);
    }

    // 🔴 404 を 403 へ置き換えないこと。GraphService と使い分けが食い違うと、BFF 層で存在が漏れる。
    [Fact]
    public async Task Backend_404_is_not_translated_to_403()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.NotFound;

        var res = await CreateAuthenticatedClient()
            .GetAsync($"/bff/graph/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        res.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "403 は『権限が無いだけで存在はする』ことを漏らす");
    }

    // B-04 / B-05: 近傍探索も同じ経路を通り、資格情報が届く。
    [Fact]
    public async Task Neighbors_forwards_credentials_and_hops()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubGraphView = new GraphViewDto([], [], false);
        var id = Guid.NewGuid();

        var res = await CreateAuthenticatedClient()
            .GetAsync($"/bff/graph/{id}/neighbors?hops=3", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastGraphForwardedAuthorization.Should().NotBeNullOrEmpty();
        _factory.LastGraphPath.Should().Contain("hops=3", "hops はそのまま後段へ渡す");
        _factory.LastGraphPath.Should().Contain($"/graph/{id}/neighbors");
    }

    // FR-17, SC-18 (#917): 間引き基準と辺の型フィルタも**そのまま**後段へ渡す。
    // 検証（types の形式不正 400 を含む）は GraphService の一箇所であり、BFF は正規化しない。
    [Fact]
    public async Task Neighbors_forwards_thinning_and_edge_type_filter()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubGraphView = new GraphViewDto([], [], false);
        var (typeA, typeB) = (Guid.NewGuid(), Guid.NewGuid());

        var res = await CreateAuthenticatedClient().GetAsync(
            $"/bff/graph/{Guid.NewGuid()}/neighbors?by=updated&types={typeA},{typeB}",
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastGraphPath.Should().Contain("by=updated");
        _factory.LastGraphPath.Should().Contain($"types={typeA}%2C{typeB}",
            "types はエスケープ以外の変形をせずに渡す");
    }

    // FR-17, SC-18 陽性対照の対（上のテストの裏面）: フィルタ未指定なら types を**足さない**。
    // 空の types を送ると「空 = 全部落とす」か「空 = 絞らない」かの解釈が後段依存になる。
    [Fact]
    public async Task Neighbors_without_filter_sends_no_types_parameter()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.StubGraphView = new GraphViewDto([], [], false);

        var res = await CreateAuthenticatedClient().GetAsync(
            $"/bff/graph/{Guid.NewGuid()}/neighbors", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastGraphPath.Should().NotContain("types=");
    }

    // B-06: 深さ上限の超過は 400 のまま透過する。**BFF で握り潰さない。**
    [Fact]
    public async Task Hops_out_of_range_400_passes_through()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.BadRequest;

        var res = await CreateAuthenticatedClient()
            .GetAsync($"/bff/graph/{Guid.NewGuid()}/neighbors?hops=4", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "200 や 500 へ潰すと、利用者は上限を越えたことを知れない");
    }

    // 後段へ到達できないときは 502。**空応答へ縮退しない** ——
    // グラフが「空」と「引けない」は利用者にとって別の意味である。
    [Fact]
    public async Task Backend_error_is_not_degraded_to_an_empty_graph()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.InternalServerError;

        var res = await CreateAuthenticatedClient()
            .GetAsync($"/bff/graph/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        res.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "空のグラフを返すと『関係が無い』と読めてしまう");
    }

    // B-07: 未認証は 401（BFF の入口で止まり、後段へ行かない）。
    [Fact]
    public async Task Unauthenticated_is_401_and_never_reaches_the_backend()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.GetAsync($"/bff/graph/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
