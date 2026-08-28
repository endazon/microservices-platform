using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Bff.Tests;

// FR-18, UC-10, SC-03, SC-21, ADR-0033, ADR-0034 (#918 / #450): AI 提案の BFF 公開。
//
// 🔴 **「GraphService 側で効いているから BFF 経由でも効く」は測った証拠にならない**（#952 の教訓）。
// 認可と存在秘匿の境界を跨ぐので、BFF 層で改めて測る。
//
// 🔴 **［2026-08-29 / #450］承認・却下の口が開いた。** #918 の時点で開いていたのは読み取りだけで、
// 本ファイルはそれを「メソッドが GET だけ」という形で固定していた。**承認欄（SC-03）が来た以上、
// その主張は事実に反する。** 一方で**一括承認をどの層にも作らないという固定は残さねばならない**
// （FR-18・SC-21「描いてはいけないもの」）。両者を分けるため、主張を
// 「**単票の承認・却下は在る（陽性対照）／一括承認のパターンに一致するルートは 1 本も無い**」へ
// 組み替えた（[[IADR-0300]] 決定 5）。生成（`generate/...`）は引き続き公開しない。
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

    // 🔴 FR-18・SC-21「描いてはいけないもの」: **一括承認・一括却下の口はどの層にも無い。**
    //
    // 陽性対照を 2 段で置く。①提案のルートが取れること（取れなければ否定形は自明に成り立つ）、
    // ②**単票の承認・却下が在ること**（#450 で開けた。無ければ「一括が無い」は測れていない）。
    //
    // 🔴 **「メソッドが GET だけ」では固定できない。** 承認・却下が開いた今、その主張は
    // 「書き込みが 1 本も無い」を意味してしまい、**一括承認を単票の隣へ足しても落ちない**。
    // 判定は**パターンで行う** —— 一括の口は必ず「1 件の ID を取らない承認・却下」の形になる。
    [Fact]
    public void No_bulk_approval_route_for_suggestions_is_exposed_by_the_bff()
    {
        using var scope = _factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<IEnumerable<EndpointDataSource>>();
        var endpoints = sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();

        endpoints.Should().NotBeEmpty("ルートが 1 本も取れないと本テストは空振りする");

        var suggestionRoutes = endpoints
            .Where(e => (e.RoutePattern.RawText ?? string.Empty)
                .Contains("suggestions", StringComparison.OrdinalIgnoreCase))
            .Select(e => new
            {
                Pattern = e.RoutePattern.RawText ?? string.Empty,
                Methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
            })
            .ToList();

        // 陽性対照 ①: 提案の口が取れている。
        suggestionRoutes.Should().NotBeEmpty(
            "提案の口が 1 本も無いなら、下の否定形は自明に成り立ってしまう（装置の検出力）");

        // 陽性対照 ②: **単票の承認・却下が在る。** これが無いと「一括が無い」は測れていない。
        suggestionRoutes.Select(r => r.Pattern).Should().Contain(
            ["/bff/graph/suggestions/{id:guid}/approve", "/bff/graph/suggestions/{id:guid}/reject"],
            "#450 で開けた単票の承認・却下（SC-03 の承認欄が呼ぶ口）");

        // 🔴 否定形: **1 件の ID を取らない承認・却下の口が無い。**
        var bulk = suggestionRoutes
            .Where(r => r.Methods.Any(m => !string.Equals(m, "GET", StringComparison.OrdinalIgnoreCase)))
            .Where(r => IsApprovalLike(r.Pattern) && !TakesASingleId(r.Pattern))
            .Select(r => r.Pattern)
            .ToList();

        bulk.Should().BeEmpty(
            "FR-18・SC-21 は一括承認を禁じている。"
            + "承認は 1 件ずつ（両端の文書の内容を見て判断する）であり、"
            + "タイトルだけを見て機械的に承認する運用に落とす口を作らない");

        // 生成の口は引き続き公開しない（消費者となる導線が計画に無い）。
        suggestionRoutes.Select(r => r.Pattern).Should().NotContain(
            p => p.Contains("generate", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>承認・却下に当たるパスか（`approve` / `reject` / `bulk` / `all` の語で見る）。</summary>
    private static bool IsApprovalLike(string pattern) =>
        pattern.Contains("approve", StringComparison.OrdinalIgnoreCase)
        || pattern.Contains("reject", StringComparison.OrdinalIgnoreCase)
        || pattern.Contains("bulk", StringComparison.OrdinalIgnoreCase);

    /// <summary>1 件の ID をパスパラメータで取るか（取らない承認の口 ＝ 一括である）。</summary>
    private static bool TakesASingleId(string pattern) =>
        pattern.Contains("{id", StringComparison.OrdinalIgnoreCase);

    // ── FR-18, SC-03 (#450): 承認・却下の口 ──────────────────────────────────

    private static AiSuggestionDto Approved() => new(
        Guid.NewGuid(), "link", SourceId, TargetId, Guid.NewGuid(), null,
        "両文書が同じ規程を引いている", "approved", 0, null, "経費精算規程 v3.2", "旅費規程");

    private HttpClient WriteClient()
    {
        _factory.GraphWriteStubStatusCode = HttpStatusCode.OK;
        _factory.GraphWriteStubBody = null;
        _factory.GraphWriteStubThrows = false;
        _factory.StubSuggestionWriteResult = Approved();
        return CreateAuthenticatedClient();
    }

    // C-06 陽性対照: 承認が 200 を返し、**資格情報が後段へ届き**、応答本文が素通りする。
    //
    // 🔴 **本文まで見る。** 状態コードだけ見ていると、BFF が本文を捨てても緑のままである
    // （画面は承認後の `state` を読んで表示を切り替える）。
    [Fact]
    public async Task Approve_forwards_credentials_and_relays_the_body()
    {
        var res = await WriteClient().PostAsync(
            $"/bff/graph/suggestions/{Guid.NewGuid()}/approve", null,
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastGraphForwardedAuthorization.Should().NotBeNullOrEmpty(
            "伝播しないと GraphService は利用者を anonymous として解決し、承認が静かに全件 404 になる");
        _factory.LastGraphMethod.Should().Be("POST");
        var dto = await res.Content.ReadFromJsonAsync<AiSuggestionDto>(
            TestContext.Current.CancellationToken);
        dto!.State.Should().Be("approved");
        dto.SourceDocumentTitle.Should().Be("経費精算規程 v3.2");
    }

    // 却下も同じ形で通る。**本文は送らない**（指紋を公開面へ出さないため。IADR-0300 決定 3）。
    [Fact]
    public async Task Reject_forwards_credentials_and_sends_no_body()
    {
        var res = await WriteClient().PostAsync(
            $"/bff/graph/suggestions/{Guid.NewGuid()}/reject", null,
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastGraphForwardedAuthorization.Should().NotBeNullOrEmpty();
        _factory.LastGraphBody.Should().BeNullOrEmpty(
            "本文指紋は公開面に出さないので、SPA も BFF も却下の本文を持たない");
    }

    // 🔴 後段のパスは**末尾スラッシュを付けない**（一覧だけが `MapGet("/")` で生えている）。
    // 付けると 404 になり、画面には「承認できない」ではなく後段エラーとして出る。
    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task It_calls_the_backend_path_without_a_trailing_slash(string action)
    {
        var id = Guid.NewGuid();

        await WriteClient().PostAsync($"/bff/graph/suggestions/{id}/{action}", null,
            TestContext.Current.CancellationToken);

        _factory.LastGraphPath.Should().Be($"/graph/suggestions/{id}/{action}");
    }

    // 🔴 C-07: **404 を作り替えない。** 後段は「権限外・不存在・write 権限なし」をすべて 404 に
    // 倒しており（ADR-0034 決定 2 / IADR-0272 決定 3）、BFF が 403 や 200 へ変えると
    // **存在秘匿が BFF 層で破れる**（403 は「権限が無いだけで存在はする」ことを漏らす）。
    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Backend_404_is_relayed_verbatim(string action)
    {
        var client = WriteClient();
        _factory.GraphWriteStubStatusCode = HttpStatusCode.NotFound;

        var res = await client.PostAsync($"/bff/graph/suggestions/{Guid.NewGuid()}/{action}", null,
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // C-08: 409（`invalid_transition`）は**本文ごと**透過する。画面は理由で文言を出し分ける。
    [Fact]
    public async Task Backend_409_passes_through_with_its_body()
    {
        var client = WriteClient();
        _factory.GraphWriteStubStatusCode = HttpStatusCode.Conflict;
        _factory.GraphWriteStubBody = """{"error":"invalid_transition","state":"approved"}""";

        var res = await client.PostAsync($"/bff/graph/suggestions/{Guid.NewGuid()}/approve", null,
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("invalid_transition");
    }

    // C-09: 400（`unknown_edge_type`）も同様。**辺の型が消えている**ことを画面へ伝える必要がある。
    [Fact]
    public async Task Backend_400_passes_through_with_its_body()
    {
        var client = WriteClient();
        _factory.GraphWriteStubStatusCode = HttpStatusCode.BadRequest;
        _factory.GraphWriteStubBody = """{"error":"unknown_edge_type"}""";

        var res = await client.PostAsync($"/bff/graph/suggestions/{Guid.NewGuid()}/approve", null,
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("unknown_edge_type");
    }

    // C-10: 未認証は BFF の入口で 401。後段へ行かない。
    [Fact]
    public async Task Unauthenticated_write_is_401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var res = await client.PostAsync($"/bff/graph/suggestions/{Guid.NewGuid()}/approve", null,
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // C-11: 後段へ到達できないときに**成功へ縮退しない。**
    // 承認できていないのに承認済みと見えるのが最悪である（辺が生まれたと誤認して棚卸しが進む）。
    [Fact]
    public async Task Backend_unreachable_is_not_degraded_to_success()
    {
        var client = WriteClient();
        _factory.GraphWriteStubThrows = true;

        var res = await client.PostAsync($"/bff/graph/suggestions/{Guid.NewGuid()}/approve", null,
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }
}
