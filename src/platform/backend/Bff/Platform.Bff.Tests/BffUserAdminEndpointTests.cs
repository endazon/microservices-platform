using AwesomeAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Platform.Bff.Tests;

// FR-05, FR-09, UC-05, SC-17, ADR-0026, IADR-0301, #452: /bff/admin/users が
// AuthorizationService の管理 API へ AdminOnly で中継することを検証する。
//
// 固定する性質は 4 つで、**どれも「拒否の側だけ」では測れない**ので陽性対照と対で置く。
//   1. **ロール限定**（05_screens §共通シェル「SC-09・SC-12・SC-17 = システム管理者」）:
//      管理者は 200 / 運用者は 403 / 無認証は 401。**管理者が通ることを先に確かめる。**
//   2. **状態コードの透過**: 後段の 400（検証エラー）・404（不在）をそのまま返す。作り替えない。
//   3. **資格情報の伝播**: 後段も自分で AdminOnly を強制する二重ゲートである。
//   4. 🔴 **新規作成の口が無いこと**（計画 05_screens §SC-17）。陽性対照つきで測る。
public class BffUserAdminEndpointTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffUserAdminEndpointTests(BffTestFactory factory)
    {
        _factory = factory;
        // IClassFixture は共有される。観測する側が既定へ戻す。
        _factory.UserAdminStatusCode = HttpStatusCode.OK;
        _factory.AuthzManagementThrows = false;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private HttpClient AsAdmin()
    {
        var client = _factory.CreateClient();
        // `X-Test-Roles` は BFF の認証、`Authorization` は後段への伝播。**別物なので明示的に付ける。**
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        return client;
    }

    private HttpClient AsOperator()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        return client;
    }

    private HttpClient Anonymous()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        return client;
    }

    // ── 1. 陽性対照: 管理者は 6 端点すべてを使える ──────────────────────

    [Fact]
    public async Task ListUsers_AsAdmin_ReturnsUsersAndStripsTheBffPrefix()
    {
        var resp = await AsAdmin().GetAsync("/bff/admin/users", Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync(Ct)).Should().Contain("tanaka.taro");
        _factory.LastUserAdminPath.Should().Be("/authz/users");
        // 二重ゲート: 資格情報が後段へ渡っている。
        _factory.LastUserAdminForwardedAuthorization.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task AssignableRoles_AsAdmin_ReachesTheValueDomainEndpoint()
    {
        var resp = await AsAdmin().GetAsync("/bff/admin/users/assignable-roles", Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync(Ct)).Should().Contain("platform-admin");
        _factory.LastUserAdminPath.Should().Be("/authz/users/assignable-roles");
    }

    [Fact]
    public async Task ReplaceAttributes_AsAdmin_UsesPutAndForwardsBody()
    {
        var resp = await AsAdmin().PutAsync("/bff/admin/users/u-tanaka/attributes",
            Json("""{"attributes":{"department":"finance","clearance":"internal"}}"""), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastUserAdminMethod.Should().Be("PUT");
        _factory.LastUserAdminPath.Should().Be("/authz/users/u-tanaka/attributes");
        // 🔴 本文を落とすと後段は常に「必須属性が未設定」で 400 を返す。
        _factory.LastUserAdminBody.Should().Contain("clearance");
    }

    [Fact]
    public async Task ReplaceRoles_AsAdmin_UsesPutAndForwardsBody()
    {
        var resp = await AsAdmin().PutAsync("/bff/admin/users/u-tanaka/roles",
            Json("""{"roles":["platform-admin","platform-operator"]}"""), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastUserAdminPath.Should().Be("/authz/users/u-tanaka/roles");
        _factory.LastUserAdminBody.Should().Contain("platform-operator");
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("enable")]
    public async Task ToggleAccount_AsAdmin_HitsTheDownstreamPath(string action)
    {
        var resp = await AsAdmin().PostAsync($"/bff/admin/users/u-tanaka/{action}", content: null, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LastUserAdminPath.Should().Be($"/authz/users/u-tanaka/{action}");
    }

    // ── 2. ロール限定（05_screens §共通シェル: システム管理者ロール限定） ───────

    [Theory]
    [InlineData("/bff/admin/users")]
    [InlineData("/bff/admin/users/assignable-roles")]
    public async Task Reads_AsOperator_AreForbidden(string path)
        => (await AsOperator().GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Writes_AsOperator_AreForbidden()
    {
        (await AsOperator().PostAsync("/bff/admin/users/u-tanaka/disable", content: null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsOperator().PutAsync("/bff/admin/users/u-tanaka/roles",
                Json("""{"roles":["platform-admin"]}"""), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
        => (await Anonymous().GetAsync("/bff/admin/users", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    // ── 3. 状態コードの透過（作り替えない） ────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task DownstreamStatus_IsPassedThroughUnchanged(HttpStatusCode status)
    {
        _factory.UserAdminStatusCode = status;

        var resp = await AsAdmin().PutAsync("/bff/admin/users/u-ghost/roles",
            Json("""{"roles":["platform-admin"]}"""), Ct);

        resp.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task DownstreamUnreachable_Becomes502()
    {
        _factory.AuthzManagementThrows = true;
        try
        {
            (await AsAdmin().GetAsync("/bff/admin/users", Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
        finally
        {
            _factory.AuthzManagementThrows = false;
        }
    }

    // ── 4. 🔴 新規作成の口が無いこと（計画 05_screens §SC-17） ──────────────

    [Fact]
    public async Task There_is_no_user_creation_endpoint()
    {
        // 陽性対照を先に置く: 同じ接頭辞の読み取りは 200 である（＝経路自体は生きている）。
        (await AsAdmin().GetAsync("/bff/admin/users", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await AsAdmin().PostAsync("/bff/admin/users",
            Json("""{"username":"new.user"}"""), Ct);

        // **405（Method Not Allowed）である。** 経路 `/bff/admin/users` は GET として在るが、
        // POST の登録が 1 つも無いのでルーティングが動詞で断る。404 ではないのは経路が実在する
        // からであり、**405 のほうが「作成の口だけが無い」ことの証拠として強い**
        // （404 だと「経路ごと消えた」と区別が付かない）。
        resp.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}
