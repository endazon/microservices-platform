using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;

namespace Platform.Bff.Tests;

// FR-22, UC-11, ADR-0037, IADR-0215 / IADR-0267 / IADR-0347, #600:
// `/bff/notifications*` が NotificationService へ中継することを検証する。
//
// 固定する性質は 5 つで、**どれも「拒否の側だけ」では測れない**ので陽性対照と対で置く。
//   1. **認証必須・ロールは問わない**: 未認証は 401（陰性）／一般利用者ロールでも 200（陽性）。
//      🔴 ここへロールを足すと**全利用者が自分の通知を読めなくなる**。狭めすぎていない側を先に固定する。
//   2. **資格情報の伝播**: 後段は主体をトークンからしか採らない。伝播が切れると全部 401 になるので、
//      スタブが観測して陽性対照にする。
//   3. **クエリの載せ替え**: `unreadOnly` / `limit` が後段のクエリへ載る。落とすと未読フィルタが
//      **無言で効かなくなる**（一覧は返るので画面上は壊れて見えない）。
//   4. **状態コードの透過**: 後段の 404（存在秘匿）をそのまま返す。403 や 200 へ変換すると
//      **存在秘匿が BFF 層で破れる**。既読化の 200（冪等）も透過する。
//   5. **不達は 502**（空の 200 で隠さない。「通知が 0 件になった」と読ませない）。
public class BffNotificationEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    private const string OwnNotificationId = "22222222-2222-2222-2222-222222222222";

    public BffNotificationEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        // IClassFixture は共有される。観測する側が既定へ戻す。
        _factory.NotificationStubStatusCode = HttpStatusCode.OK;
        _factory.NotificationStubThrows = false;
    }

    /// <summary>
    /// 認証済みの呼び出し口。
    ///
    /// 🔴 **`TestAuthHandler` が扱うのはヘッダ `X-Test-Roles` であり、後段へ伝播されるのは
    /// `Authorization` である。両者は別物なので明示的に付ける**（`BffMcpClientEndpointTests` と同じ作法）。
    /// 付けないと後段スタブは「資格情報が届いていない」と判定して 401 を返す ——
    /// これは実サービスの挙動そのものであり、**伝播の陽性対照が成立していることの裏返し**である。
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

    // ── 1. 陽性対照: 認証済みなら一覧が返り、資格情報が後段へ届いている（AC-7）──────

    [Fact]
    public async Task List_WhenAuthenticated_ReturnsNotificationsAndForwardsCredentials()
    {
        var resp = await Authenticated().GetAsync("/bff/notifications", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("unreadCount");
        // 後段のパスは `/notifications`（BFF の接頭辞 `/bff` は剥がして中継する）。
        _factory.LastNotificationPath.Should().Be("/notifications");
        // 🔴 本人絞りの実体。落とすと後段は主体を決められず、機能が丸ごと 401 で死ぬ。
        _factory.LastNotificationForwardedAuthorization.Should().Be("Bearer test-token");
    }

    // 🔴 **狭めすぎていない側**（AC-11）。通知は全利用者が受け取る（契約の `x-roles: []`）。
    // ここへロールを足すと、一般利用者が削除予告・容量警告を受け取れなくなる。
    [Fact]
    public async Task List_AsNonPrivilegedRole_IsAllowed()
    {
        var resp = await Authenticated("viewer").GetAsync("/bff/notifications", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "通知は役割ではなく主体で絞る（x-roles: []）");
    }

    // ── 2. クエリの載せ替え（AC-7b）──────────────────────────────

    [Fact]
    public async Task List_WithFilters_ForwardsQueryToDownstream()
    {
        var resp = await Authenticated()
            .GetAsync("/bff/notifications?unreadOnly=true&limit=10", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // 落とすと未読フィルタが無言で効かなくなる（一覧自体は返るため画面上は壊れて見えない）。
        _factory.LastNotificationPath.Should().Be("/notifications?unreadOnly=true&limit=10");
    }

    // **指定が無いときは何も載せない**（既定値を BFF が埋めない）。既定 50 / 上限 100 の
    // クランプは後段の `NotificationOptions` が唯一の情報源である（IADR-0347 決定 4）。
    [Fact]
    public async Task List_WithoutFilters_DoesNotInventDefaults()
    {
        _ = await Authenticated().GetAsync("/bff/notifications", TestContext.Current.CancellationToken);

        _factory.LastNotificationPath.Should().NotContain("limit");
        _factory.LastNotificationPath.Should().NotContain("unreadOnly");
    }

    // ── 3. 既読化: 冪等な 200 の透過（AC-9）と 404 の透過（AC-8）───────────

    [Fact]
    public async Task MarkRead_WhenAuthenticated_ForwardsToDownstreamReadPath()
    {
        var resp = await Authenticated().PostAsync(
            $"/bff/notifications/{OwnNotificationId}/read", content: null, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "既読化は冪等であり、後段の 200 を透過する");
        _factory.LastNotificationMethod.Should().Be("POST");
        _factory.LastNotificationPath.Should().Be($"/notifications/{OwnNotificationId}/read");
    }

    // 🔴 後段は「存在しない」と「本人のものでない」を区別せず 404 を返す（存在秘匿。IADR-0009）。
    // BFF がこれを 403 へ変えると**他人の通知 ID の実在が漏れる**。200 へ変えると既読化の失敗が隠れる。
    [Fact]
    public async Task MarkRead_WhenDownstreamReturnsNotFound_RelaysNotFound()
    {
        _factory.NotificationStubStatusCode = HttpStatusCode.NotFound;

        var resp = await Authenticated().PostAsync(
            $"/bff/notifications/{OwnNotificationId}/read", content: null, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "存在秘匿を BFF 層で破らない");
    }

    // ── 4. 未認証は 401（AC-10。エッジで認証を担保する）───────────────────

    [Fact]
    public async Task List_WhenAnonymous_Returns401()
    {
        var resp = await Anonymous().GetAsync("/bff/notifications", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MarkRead_WhenAnonymous_Returns401()
    {
        var resp = await Anonymous().PostAsync(
            $"/bff/notifications/{OwnNotificationId}/read", content: null, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── 5. 後段不達は 502（AC-12）────────────────────────────────

    [Fact]
    public async Task List_WhenDownstreamUnreachable_Returns502()
    {
        _factory.NotificationStubThrows = true;

        var resp = await Authenticated().GetAsync("/bff/notifications", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "空の 200 で隠すと「通知が 0 件になった」と読ませてしまう");
    }

    // ── 6. 上流解決（AC-13）—— #342 と同型の直書き退行を止める ───────────────

    // `BffDownstreamResolutionTests` と同型。named client の BaseAddress が
    // `Services:NotificationService` 設定で解決される（コード既定の直書きへ退行していない）。
    [Fact]
    public void NotificationService_client_base_address_is_resolved_from_Services_configuration()
    {
        using var factory = new BffTestFactory();
        var httpFactory = factory.Services.GetRequiredService<IHttpClientFactory>();

        var client = httpFactory.CreateClient("NotificationService");

        client.BaseAddress.Should().Be(new Uri("http://notification-service:8080"),
            "NotificationService の上流は Services:NotificationService 設定で解決されるべき（#342 と同型の退行防止）");
    }
}
