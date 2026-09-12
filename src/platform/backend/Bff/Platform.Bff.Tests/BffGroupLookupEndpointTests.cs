using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Platform.Bff.Tests;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1・3, ADR-0100 決定 1・フォローアップ 2,
// [[IADR-0447]] / [[IADR-0449]], #1447:
// `/bff/groups/lookup`・`/bff/groups/resolve` が AuthorizationService の同名の口へ
// **ロール不問・ただし人の主体だけ**で透過中継することを固定する。
//
// 固定する性質は 5 つで、**どれも片側だけでは測れない**ので陽性対照と対で置く
// （`BffUserLookupEndpointTests` と**同じ形**である —— 画面 SC-19 は指定先の種別を
// 切り替えるだけなので、2 つの口の作法が違うと画面側に分岐が生える）。
//   1. **一般利用者で通る**（共有先を選ぶのは所有者である）。
//   2. 🔴 **サービスアカウントは 403**（`platform-admin` を持つものも 403。受け入れ基準 6）。
//   3. **無認証は 401**（`/bff/*` の不変条件）。
//   4. **資格情報の伝播**（後段も自分で認可を見る二重ゲート）＋**後段のパスとクエリ**。
//   5. **後段不達は 502**（空で隠さない）。
public class BffGroupLookupEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffGroupLookupEndpointTests(BffTestFactory factory)
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

    // Keycloak のサービスアカウント（`preferred_username = service-account-<clientId>`）。
    private HttpClient AsServiceAccount(params string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UsernameHeader, "service-account-platform");
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.RolesHeader,
            string.Join(',', roles.Length == 0 ? ["platform-service"] : roles));
        return client;
    }

    private HttpClient Anonymous()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        return client;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path)
        => method == "POST"
            ? client.PostAsJsonAsync(path, new ResolveGroupsRequest(["g-knowledge"]), Ct)
            : client.GetAsync(path, Ct);

    // ── 1 / 4. 一般利用者で通り、クエリと資格情報が後段へ届く ────────────────

    [Fact]
    public async Task Lookup_AsRegularUser_ReachesTheDownstreamWithTheQueryString()
    {
        var resp = await AsRegularUser().GetAsync("/bff/groups/lookup?q=know&limit=5", Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<List<GroupSummaryDto>>(Ct))
            .Should().Contain(g => g.Id == "g-knowledge");
        // 🔴 **クエリを落とすと後段は常に 400（q が 2 文字未満）を返す。**
        _factory.LastGroupLookupPath.Should().Be("/authz/groups/lookup");
        _factory.LastGroupLookupForwardedAuthorization.Should().Be("Bearer regular-user-token");
    }

    [Fact]
    public async Task Resolve_AsRegularUser_ForwardsTheBody()
    {
        var resp = await AsRegularUser().PostAsJsonAsync("/bff/groups/resolve",
            new ResolveGroupsRequest(["g-knowledge", "g-finance"]), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastGroupLookupPath.Should().Be("/authz/groups/resolve");
        _factory.LastGroupLookupMethod.Should().Be("POST");
        // 🔴 本文を落とすと後段は常に 400（ids が空）を返す。
        _factory.LastGroupLookupBody.Should().Contain("g-finance");

        // 面は 3 項目だけである（所属者・属性を運ぶ型ではない）。
        var groups = await resp.Content.ReadFromJsonAsync<List<GroupSummaryDto>>(Ct);
        groups.Should().AllSatisfy(g => g.Path.Should().StartWith("/"));
    }

    // 🔴 **利用者側の口とは別の後段パスへ行く**（取り違えの検出）。
    [Fact]
    public async Task The_group_face_does_not_reach_the_user_lookup_downstream()
    {
        await AsRegularUser().GetAsync("/bff/groups/lookup?q=know", Ct);

        _factory.LastGroupLookupPath.Should().Be("/authz/groups/lookup");
        _factory.LastUserAdminPath.Should().NotBe("/authz/groups/lookup");
    }

    // ── 2. 🔴 サービスアカウントは 403（受け入れ基準 6）─────────────────────

    [Theory]
    [InlineData("GET", "/bff/groups/lookup?q=know")]
    [InlineData("POST", "/bff/groups/resolve")]
    public async Task A_service_account_is_forbidden(string method, string path)
        => (await Send(AsServiceAccount(), method, path)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

    // 🔴 **`platform-admin` を持つサービスアカウントも 403**（ロールではなく主体の種別で分ける）。
    [Theory]
    [InlineData("GET", "/bff/groups/lookup?q=know")]
    [InlineData("POST", "/bff/groups/resolve")]
    public async Task A_service_account_holding_the_admin_role_is_still_forbidden(string method, string path)
        => (await Send(AsServiceAccount("platform-admin"), method, path)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

    // 陽性対照（2 の対）: 同じ 2 経路が人では 200 である（「常に拒否」ではない）。
    [Theory]
    [InlineData("GET", "/bff/groups/lookup?q=know")]
    [InlineData("POST", "/bff/groups/resolve")]
    public async Task A_human_subject_reaches_both_endpoints(string method, string path)
        => (await Send(AsRegularUser(), method, path)).StatusCode
            .Should().Be(HttpStatusCode.OK);

    // ── 3. 無認証は 401 ──────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/bff/groups/lookup?q=know")]
    [InlineData("POST", "/bff/groups/resolve")]
    public async Task Anonymous_requests_are_rejected_with_401(string method, string path)
        => (await Send(Anonymous(), method, path)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

    // ── 4b. 後段の状態は作り替えない（検証 400 をそのまま画面へ返す）───────────

    [Fact]
    public async Task DownstreamValidationStatus_IsPassedThroughUnchanged()
    {
        _factory.UserAdminStatusCode = HttpStatusCode.BadRequest;
        try
        {
            // 後段が「q が 2 文字未満」で拒む形。**前段で写しの検証をしていない**ことの確認でもある。
            (await AsRegularUser().GetAsync("/bff/groups/lookup?q=k", Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            _factory.UserAdminStatusCode = HttpStatusCode.OK;
        }
    }

    // ── 5. 後段不達は 502（空で隠さない）──────────────────────────────

    [Fact]
    public async Task DownstreamUnreachable_Becomes502()
    {
        _factory.AuthzManagementThrows = true;
        try
        {
            (await AsRegularUser().GetAsync("/bff/groups/lookup?q=know", Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
        finally
        {
            _factory.AuthzManagementThrows = false;
        }
    }

    // 🔴 **書き込みの口を持たない**（グループ木は管理者が Keycloak で作る。ADR-0098 決定 3）。
    [Fact]
    public async Task There_is_no_write_endpoint_on_the_group_lookup_group()
    {
        // 陽性対照を先に置く: 読み取りは 200 である（＝経路自体は生きている）。
        (await AsRegularUser().GetAsync("/bff/groups/lookup?q=know", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var put = await AsRegularUser().PutAsJsonAsync("/bff/groups/lookup",
            new { name = "new-group" }, Ct);

        // 経路は在るが PUT の登録が無いのでルーティングが動詞で断る（404 ではない）。
        put.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}
