using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0445]], #1445:
// `/bff/users/lookup`・`/bff/users/resolve` が AuthorizationService の同名の口へ
// **認証のみ・ロール不問**で透過中継することを固定する。
//
// 固定する性質は 4 つで、**どれも片側だけでは測れない**ので陽性対照と対で置く。
//   1. **一般利用者で通る**（共有先を選ぶのは所有者である）。**同時に、`/bff/admin/users`
//      （AdminOnly の管理面）は一般利用者で 403 のままである** —— 群を取り違えると、
//      ここが緑のまま管理面が開く（またはこちらが 403 になる）。
//   2. **無認証は 401**（`/bff/*` の不変条件）。
//   3. **資格情報の伝播**（後段も自分で認可を見る二重ゲート）＋**後段のパスとクエリ**。
//   4. **後段不達は 502**（空で隠さない）。
public class BffUserLookupEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffUserLookupEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        // IClassFixture は共有される。観測する側が既定へ戻す。
        _factory.UserAdminStatusCode = HttpStatusCode.OK;
        _factory.AuthzManagementThrows = false;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 管理者ロールを持たない一般利用者。`Authorization` は後段への伝播用に明示的に載せる。
    private HttpClient AsRegularUser()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "regular-user-token");
        return client;
    }

    private HttpClient Anonymous()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        return client;
    }

    // ── 1. 一般利用者で通る ────────────────────────────────────────

    [Fact]
    public async Task Lookup_AsRegularUser_ReachesTheDownstreamWithTheQueryString()
    {
        var resp = await AsRegularUser().GetAsync("/bff/users/lookup?q=tanaka&limit=5", Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<List<UserSummaryDto>>(Ct))
            .Should().Contain(u => u.Username == "tanaka.taro");
        // 🔴 **クエリを落とすと後段は常に 400（q が 2 文字未満）を返す。**
        _factory.LastUserAdminPath.Should().Be("/authz/users/lookup");
        _factory.LastUserAdminForwardedAuthorization.Should().Be("Bearer regular-user-token");
    }

    [Fact]
    public async Task Resolve_AsRegularUser_ForwardsTheBody()
    {
        var resp = await AsRegularUser().PostAsJsonAsync("/bff/users/resolve",
            new ResolveUsersRequest(["tanaka.taro", "takahashi.jiro"]), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastUserAdminPath.Should().Be("/authz/users/resolve");
        _factory.LastUserAdminMethod.Should().Be("POST");
        // 🔴 本文を落とすと後段は常に 400（usernames が空）を返す。
        _factory.LastUserAdminBody.Should().Contain("takahashi.jiro");

        // 無効化済み（退職者）も返る（`enabled=false` で画面が区別する。ADR-0098 決定 1）。
        var users = await resp.Content.ReadFromJsonAsync<List<UserSummaryDto>>(Ct);
        users.Should().Contain(u => !u.Enabled);
    }

    // 🔴 **陽性対照（逆向き）: 管理面の認可は変わっていない。**
    [Fact]
    public async Task The_admin_user_management_stays_forbidden_for_a_regular_user()
        => (await AsRegularUser().GetAsync("/bff/admin/users", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    // ── 2. 無認証は 401 ──────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/bff/users/lookup?q=tanaka")]
    [InlineData("POST", "/bff/users/resolve")]
    public async Task Anonymous_requests_are_rejected_with_401(string method, string path)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new ResolveUsersRequest(["tanaka.taro"])),
        };

        (await Anonymous().SendAsync(req, Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── 3. 後段の状態は作り替えない（検証 400 をそのまま画面へ返す）────────────

    [Fact]
    public async Task DownstreamValidationStatus_IsPassedThroughUnchanged()
    {
        _factory.UserAdminStatusCode = HttpStatusCode.BadRequest;
        try
        {
            // 後段が「q が 2 文字未満」で拒む形。**前段で写しの検証をしていない**ことの確認でもある。
            (await AsRegularUser().GetAsync("/bff/users/lookup?q=t", Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            _factory.UserAdminStatusCode = HttpStatusCode.OK;
        }
    }

    // ── 4. 後段不達は 502（空で隠さない）───────────────────────────

    [Fact]
    public async Task DownstreamUnreachable_Becomes502()
    {
        _factory.AuthzManagementThrows = true;
        try
        {
            (await AsRegularUser().GetAsync("/bff/users/lookup?q=tanaka", Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
        finally
        {
            _factory.AuthzManagementThrows = false;
        }
    }

    // 🔴 **書き込みの口を持たない**（利用者の作成・変更はこの群に無い）。
    [Fact]
    public async Task There_is_no_write_endpoint_on_the_lookup_group()
    {
        // 陽性対照を先に置く: 読み取りは 200 である（＝経路自体は生きている）。
        (await AsRegularUser().GetAsync("/bff/users/lookup?q=tanaka", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var put = await AsRegularUser().PutAsJsonAsync("/bff/users/lookup",
            new { username = "new.user" }, Ct);

        // 経路は在るが PUT の登録が無いのでルーティングが動詞で断る（404 ではない）。
        put.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}
