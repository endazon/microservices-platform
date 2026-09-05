using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace Platform.Bff.Tests;

// FR-17, SC-09, ADR-0033 決定 3・9, INDEX 決定 18 (#1241): 辺の型辞書の BFF（`/bff/edge-types`）。
//
// 🔴 **`BffGraphEdgeTypeTests`（`/bff/graph/edge-types` ＝描画用カタログ）とは別の口を測る。**
// 取り違えると、一般利用者が 403 になるか、ABAC で絞られていない集計値が漏れるかのどちらかになる。
// **その取り違えを落とすテストを、両側に 1 本ずつ置く**（あちらの
// `It_calls_the_catalog_path_not_the_admin_listing` と対である）。
public class BffEdgeTypeDictionaryTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffEdgeTypeDictionaryTests(BffTestFactory factory)
    {
        // 共有 fixture を汚さない（他のテストが立てた状態を持ち越さない）。
        _factory = factory;
        _factory.GraphStubStatusCode = HttpStatusCode.OK;
        _factory.EdgeTypeWriteStatusCode = HttpStatusCode.OK;
        _factory.EdgeTypeWriteStubBody = null;
        _factory.StubEdgeTypeWriteResult = null;
        _factory.StubEdgeTypeDeleteUsageCount = 0;
        _factory.StubEdgeTypeDictionary =
        [
            new EdgeTypeDto(BffTestFactory.StubEdgeTypeId, "cites", "core", false, true, 342),
            new EdgeTypeDto(Guid.NewGuid(), "part-of", "recommended", false, false, 0),
        ];
    }

    private static string DetailPath => $"/bff/edge-types/{BffTestFactory.StubEdgeTypeId}";

    // 既定の client は platform-admin（ヘッダ無し）。GraphService のスタブは Authorization を
    // 見て 401 を返すので、**資格情報も載せる**（後段が自分で ABAC を解決する型のため）。
    private HttpClient ClientAs(string? role = null)
    {
        var client = _factory.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, role);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer test-token");
        return client;
    }

    [Fact]
    public async Task List_AsAdmin_ReturnsDictionaryWithUsageCounts()
    {
        var resp = await ClientAs().GetAsync("/bff/edge-types", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await resp.Content.ReadFromJsonAsync<List<EdgeTypeDto>>(
            TestContext.Current.CancellationToken);

        // ★ 陽性対照: **使用件数が届く。** これが 0 や欠落なら、カタログ（件数なし）へ
        // 向いてしまっている——SC-09 の「削除前に使用件数を示す」が満たせなくなる。
        items!.Should().Contain(t => t.Name == "cites" && t.UsageCount == 342);
    }

    // 🔴 **取り違えの検査。** 後段は `/graph/edge-types`（辞書）であって
    // `/graph/edge-types/catalog`（描画用）ではない。
    [Fact]
    public async Task It_calls_the_admin_listing_not_the_catalog_path()
    {
        await ClientAs().GetAsync("/bff/edge-types", TestContext.Current.CancellationToken);

        _factory.LastGraphPath.Should().Be("/graph/edge-types");
        _factory.LastGraphPath.Should().NotEndWith("/catalog");
    }

    [Fact]
    public async Task Create_AsAdmin_Returns201()
    {
        _factory.EdgeTypeWriteStatusCode = HttpStatusCode.Created;
        _factory.StubEdgeTypeWriteResult =
            new EdgeTypeDto(Guid.NewGuid(), "refines", "recommended", false, false, 0);

        var resp = await ClientAs().PostAsJsonAsync(
            "/bff/edge-types",
            new { name = "refines", layer = "recommended", isSymmetric = false },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.LastGraphPath.Should().Be("/graph/edge-types");
        _factory.LastGraphMethod.Should().Be("POST");
    }

    // ADR-0033 決定 9: 改名しても**辺は 1 本も書き換わらない**（辺は型 ID を参照している）。
    // BFF から見えるのは「改名の応答がそのまま返る」ことだけであり、**BFF が追随を代行しない**
    // ことがここで固定される（代行し始めると、正本が 2 つになる）。
    [Fact]
    public async Task Rename_AsAdmin_RelaysTheRenamedTypeAndKeepsTheId()
    {
        _factory.StubEdgeTypeWriteResult =
            new EdgeTypeDto(BffTestFactory.StubEdgeTypeId, "cites-to", "core", false, true, 342);

        var resp = await ClientAs().PutAsJsonAsync(
            DetailPath, new { name = "cites-to" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<EdgeTypeDto>(
            TestContext.Current.CancellationToken);
        dto!.Name.Should().Be("cites-to");
        // **識別子は変わらない。** ここが変わると既存の辺が迷子になる。
        dto.Id.Should().Be(BffTestFactory.StubEdgeTypeId);
        dto.UsageCount.Should().Be(342, "改名は辺を書き換えないので使用件数も変わらない");
        _factory.LastGraphMethod.Should().Be("PUT");
    }

    [Fact]
    public async Task Delete_AsAdmin_WhenUnused_Returns204()
    {
        _factory.StubEdgeTypeDeleteUsageCount = 0;

        var resp = await ClientAs().DeleteAsync(DetailPath, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // 🔴 **本作業の主眼。** 後段の 409 と `usageCount` が**中立化されずに**画面まで届く。
    // BFF が `Results.StatusCode` で status だけ返す実装（`ProxyAsync<T>` の形）だと
    // **本文が落ち、SC-09 の「削除前に使用件数を示す」が満たせなくなる。**
    [Fact]
    public async Task Delete_AsAdmin_WhenInUse_Returns409WithUsageCount()
    {
        _factory.StubEdgeTypeDeleteUsageCount = 342;

        var resp = await ClientAs().DeleteAsync(DetailPath, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var json = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        json.GetProperty("usageCount").GetInt32().Should().Be(342);
        json.GetProperty("error").GetString().Should().Be("edge_type_in_use");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task Write_WhenNameDuplicated_Relays409(string method)
    {
        _factory.EdgeTypeWriteStatusCode = HttpStatusCode.Conflict;
        _factory.EdgeTypeWriteStubBody =
            """{"error":"edge_type_exists","message":"型「cites」は既に辞書にあります。"}""";

        var client = ClientAs();
        var resp = method == "POST"
            ? await client.PostAsJsonAsync("/bff/edge-types",
                new { name = "cites", layer = "core", isSymmetric = false },
                TestContext.Current.CancellationToken)
            : await client.PutAsJsonAsync(DetailPath, new { name = "cites" },
                TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("edge_type_exists");
    }

    // 🔴 **`viewer` で引くテストでは代わりにならない。** 読み取り群は admin ＋ operator なので、
    // **`AdminOnly` を 1 つも積まなくても `viewer` は 403 になる**（#629 で同じ穴を実測した）。
    // **運用者で引くことがこの作業の検査である。**
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Write_AsOperator_IsForbidden(string method)
    {
        var client = ClientAs("platform-operator");

        var resp = method switch
        {
            "POST" => await client.PostAsJsonAsync("/bff/edge-types",
                new { name = "x", layer = "core", isSymmetric = false },
                TestContext.Current.CancellationToken),
            "PUT" => await client.PutAsJsonAsync(DetailPath, new { name = "x" },
                TestContext.Current.CancellationToken),
            _ => await client.DeleteAsync(DetailPath, TestContext.Current.CancellationToken),
        };

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 読み取り群を狭めすぎていないことの担保（SC-10 が型ごとの使用件数を出すため運用者にも開く）。
    [Fact]
    public async Task List_AsOperator_IsStillAllowed()
    {
        var resp = await ClientAs("platform-operator")
            .GetAsync("/bff/edge-types", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ★ 陰性対照: 一般利用者には辞書そのものを見せない（件数は ABAC で絞られていない集計値である）。
    [Fact]
    public async Task List_AsViewer_IsForbidden()
    {
        var resp = await ClientAs("platform-user")
            .GetAsync("/bff/edge-types", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_is_401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var resp = await client.GetAsync("/bff/edge-types", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // 後段へ到達できないときは 502 へ倒す（空の辞書に縮退させない —— 縮退させると
    // 管理者は「型が 1 つも無い」と読み、追加しようとして重複を踏む）。
    [Fact]
    public async Task Backend_error_is_not_degraded_to_an_empty_dictionary()
    {
        _factory.GraphStubStatusCode = HttpStatusCode.InternalServerError;

        var resp = await ClientAs().GetAsync("/bff/edge-types", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
