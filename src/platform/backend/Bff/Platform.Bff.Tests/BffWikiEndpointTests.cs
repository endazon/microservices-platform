using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;

namespace Platform.Bff.Tests;

// FR-13, UC-07, SC-04, ADR-0011 / ADR-0032 / ADR-0073 決定 2・4, IADR-0009 / IADR-0020 /
// IADR-0335 / IADR-0361, #1199:
// `/bff/wiki/*` の 4 経路が WikiService へ中継することを検証する。
//
// 固定する性質は 6 つで、**どれも「拒否の側だけ」では測れない**ので陽性対照と対で置く。
//   1. **認証必須・ロールは問わない**: 未認証は 4 経路とも 401（陰性）／一般利用者ロールでも 200（陽性）。
//      🔴 ここへロールを足すと**一般利用者が Wiki を 1 ページも開けなくなる**。計画 05_screens は
//      利用者グループ（SC-01〜04）を「ABAC の権限内で全利用者が利用できる」と定めている。
//   2. **資格情報の伝播**: 後段は `IWikiAccessResolver` で自分で ABAC を解決する型であり、
//      伝播が切れると**一覧・検索は空・個別は 404** になる（[[IADR-0335]] 決定 4 の短絡）。
//      🔴 **「空」は正常応答と区別が付かない**ので、スタブが観測して陽性対照にする。
//   3. **経路とクエリの載せ替え**: `/bff` を剥がして `/wiki/...` へ。`q` / `limit` は
//      **指定されたときだけ**載る（既定・上限は後段が唯一の情報源）。
//   4. **状態コードの透過**: 後段の 404（存在秘匿）・502（Wiki.js 不達）をそのまま返す。
//      403 や 200 へ変換すると**存在秘匿が BFF 層で破れる**。
//   5. **200 ＋ 空の透過**: deny-by-default の空配列を作り替えない（陽性対照として中身のある 200）。
//   6. **不達は 502**（空の 200 で隠さない。「Wiki に何も無い」と読ませない）。
public class BffWikiEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    private const string Slug = "design-policy";
    private const string MissingDocumentId = "99999999-9999-9999-9999-999999999999";

    public BffWikiEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        // IClassFixture は共有される。観測する側が既定へ戻す。
        _factory.WikiStubStatusCode = HttpStatusCode.OK;
        _factory.WikiStubThrows = false;
        _factory.WikiStubReturnsEmpty = false;
    }

    /// <summary>
    /// 認証済みの呼び出し口。
    ///
    /// 🔴 **`TestAuthHandler` が扱うのはヘッダ `X-Test-Roles` であり、後段へ伝播されるのは
    /// `Authorization` である。両者は別物なので明示的に付ける**（`BffNotificationEndpointTests` と
    /// 同じ作法）。付けないと後段スタブは「資格情報が届いていない」と判定して存在秘匿の応答を返す。
    /// </summary>
    private HttpClient Authenticated(string? roles = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private HttpClient Anonymous()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        return client;
    }

    private static string ByDoc(string documentId) => $"/bff/wiki/pages/by-doc/{documentId}";

    // ── 1. 陽性対照: 4 経路とも後段へ到達し、資格情報が届いている ─────────────

    [Fact]
    public async Task List_WhenAuthenticated_ForwardsToDownstreamAndRelaysBody()
    {
        var resp = await Authenticated().GetAsync("/bff/wiki/pages", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("design-policy");
        // 後段のパスは `/wiki/pages`（BFF の接頭辞 `/bff` は剥がして中継する）。
        _factory.LastWikiPath.Should().Be("/wiki/pages");
        // 🔴 権限判定の入力そのもの。落とすと後段は anonymous として解決し、静かに空になる。
        _factory.LastWikiForwardedAuthorization.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task Search_WhenAuthenticated_ForwardsToDownstreamAndRelaysBody()
    {
        var resp = await Authenticated()
            .GetAsync("/bff/wiki/search?q=design", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("design-policy");
        _factory.LastWikiForwardedAuthorization.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task GetBySlug_WhenAuthenticated_RelaysRenderedContent()
    {
        var resp = await Authenticated()
            .GetAsync($"/bff/wiki/pages/{Slug}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // 🔴 ゲートウェイが返すのは **Wiki.js が描画した本文**である（ADR-0073 §理由）。
        // 詰め替えると SC-04 が「Wiki.js による整理された閲覧体験」を描けない。
        body.Should().Contain("content");
        _factory.LastWikiPath.Should().Be($"/wiki/pages/{Slug}");
    }

    [Fact]
    public async Task GetByDocument_WhenAuthenticated_RelaysRenderedContent()
    {
        var resp = await Authenticated()
            .GetAsync(ByDoc(BffTestFactory.StubWikiDocumentId), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastWikiPath.Should().Be($"/wiki/pages/by-doc/{BffTestFactory.StubWikiDocumentId}");
    }

    // 🔴 **狭めすぎていない側。** 計画 05_screens は SC-01〜04 を「ABAC の権限内で全利用者が
    // 利用できる」と定めている。ここへロールを足すと一般利用者が Wiki を開けなくなる。
    [Fact]
    public async Task List_AsNonPrivilegedRole_IsAllowed()
    {
        var resp = await Authenticated("viewer")
            .GetAsync("/bff/wiki/pages", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "可視性を決めるのは役割ではなく ABAC である");
    }

    // ── 2. クエリの載せ替え ───────────────────────────────────

    [Fact]
    public async Task Search_WithFilters_ForwardsQueryToDownstream()
    {
        var resp = await Authenticated()
            .GetAsync("/bff/wiki/search?q=design&limit=5", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastWikiPath.Should().Be("/wiki/search?q=design&limit=5");
    }

    // **指定が無いときは何も載せない**（既定値を BFF が埋めない）。既定 20 / 上限 50 のクランプは
    // 後段 `SearchWikiPagesEndpoint` が唯一の情報源である。
    [Fact]
    public async Task Search_WithoutFilters_DoesNotInventDefaults()
    {
        _ = await Authenticated().GetAsync("/bff/wiki/search", TestContext.Current.CancellationToken);

        _factory.LastWikiPath.Should().Be("/wiki/search");
    }

    // ── 3. 未認証は 4 経路とも 401（NFR-09 の暫定運用＝エッジで認証を担保する）─────
    //
    // 🔴 **[[IADR-0335]] 決定 4 と矛盾しない。** 同決定は「401 にはしない。**エッジは BFF**
    // （ADR-0032 / Token Handler）であり、ここは mesh 内の後段である」と書いており、401 を置く場所と
    // して BFF を名指ししている。未認証は BFF で止まり**後段へ到達しない**ので、後段が固定した
    // 「一覧・検索は 200 ＋ 空、個別は 404」は 1 ミリも動かない。
    [Theory]
    [InlineData("/bff/wiki/pages")]
    [InlineData("/bff/wiki/search")]
    [InlineData("/bff/wiki/pages/design-policy")]
    [InlineData("/bff/wiki/pages/by-doc/99999999-9999-9999-9999-999999999999")]
    public async Task AllRoutes_WhenAnonymous_Return401(string path)
    {
        var resp = await Anonymous().GetAsync(path, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "NFR-09 の暫定運用はエッジで認証を担保する");
    }

    // ── 4. 後段の状態コードの透過 ────────────────────────────────

    // 🔴 後段は「権限外」「不存在」「アーカイブ済み」を区別せず 404 を返す（存在秘匿。IADR-0009）。
    // BFF がこれを 403 へ変えると**権限外の文書が実在することが漏れる**。
    [Fact]
    public async Task GetByDocument_WhenDownstreamReturnsNotFound_RelaysNotFound()
    {
        _factory.WikiStubStatusCode = HttpStatusCode.NotFound;

        var resp = await Authenticated().GetAsync(ByDoc(MissingDocumentId), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "存在秘匿を BFF 層で破らない");
    }

    [Fact]
    public async Task GetBySlug_WhenDownstreamReturnsNotFound_RelaysNotFound()
    {
        _factory.WikiStubStatusCode = HttpStatusCode.NotFound;

        var resp = await Authenticated()
            .GetAsync($"/bff/wiki/pages/{Slug}", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 🔴 Wiki.js へ到達できないとき後段は 502 を返す（[[IADR-0335]] 決定 2。200 ＋ 空で隠さない）。
    // BFF が 200 へ畳むと「該当が無い」と読め、**故障が権限の結果に化ける**。
    [Fact]
    public async Task Search_WhenDownstreamReturnsBadGateway_RelaysBadGateway()
    {
        _factory.WikiStubStatusCode = HttpStatusCode.BadGateway;

        var resp = await Authenticated()
            .GetAsync("/bff/wiki/search?q=design", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway, "故障を空で隠さない（IADR-0335 決定 2）");
    }

    // ── 5. 200 ＋ 空の透過（deny-by-default）と、その陽性対照 ──────────────

    [Fact]
    public async Task List_WhenDownstreamReturnsEmpty_RelaysEmptyOk()
    {
        _factory.WikiStubReturnsEmpty = true;

        var resp = await Authenticated().GetAsync("/bff/wiki/pages", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "「権限が無い」と「該当が無い」を区別させない");
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Trim().Should().Be("[]");
    }

    // 陽性対照。これが無いと「常に空を返す実装」が上のテストを通してしまう。
    [Fact]
    public async Task List_WhenDownstreamHasPages_RelaysNonEmpty()
    {
        var resp = await Authenticated().GetAsync("/bff/wiki/pages", TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Trim().Should().NotBe("[]");
    }

    // ── 6. 後段不達は 502 ──────────────────────────────────

    [Fact]
    public async Task List_WhenDownstreamUnreachable_Returns502()
    {
        _factory.WikiStubThrows = true;

        var resp = await Authenticated().GetAsync("/bff/wiki/pages", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "空の 200 で隠すと「Wiki に何も無い」と読ませてしまう");
    }

    // ── 7. 上流解決 —— #342 と同型の直書き退行を止める ─────────────────

    // `BffDownstreamResolutionTests` と同型。named client の BaseAddress が
    // `Services:WikiService` 設定で解決される（コード既定の直書きへ退行していない）。
    [Fact]
    public void WikiService_client_base_address_is_resolved_from_Services_configuration()
    {
        using var factory = new BffTestFactory();
        var httpFactory = factory.Services.GetRequiredService<IHttpClientFactory>();

        var client = httpFactory.CreateClient("WikiService");

        client.BaseAddress.Should().Be(new Uri("http://wiki-service:8080"),
            "WikiService の上流は Services:WikiService 設定で解決されるべき（#342 と同型の退行防止）");
    }
}
