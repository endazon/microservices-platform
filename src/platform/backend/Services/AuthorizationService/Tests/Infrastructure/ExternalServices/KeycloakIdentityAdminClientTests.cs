using AuthorizationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;

namespace AuthorizationService.Tests.Infrastructure.ExternalServices;

// FR-05, FR-09, SC-17, IADR-0301 (#452), IADR-0329 (#1101): Keycloak Admin REST 実装の写像を固定する。
//
// 🔴 **これは疎通の検証ではない。** ここで固定できるのは「要求の組み立て」と「応答の写し取り」
// だけであり、**緑であることは実 IdP へ反映できることを意味しない**。テスト仕様書 §区分 も同じ。
// 疎通は稼働クラスタで測る（#1101 で実測した。旧記述「realm export に未登録」は解消した）。
//
// 🔴 **下の 2 件は、実 Keycloak で測って初めて分かった罠を固定している** ——
// ①`PUT /users/{id}` は部分更新ではない（送らない項目が消える）。②realm が unmanaged 属性の
// 書き込みを許していないと 204 を返しながら黙って捨てる。**どちらもスタブでは自然には出ない**
// ので、実測した挙動をスタブ側に再現して固定する。
public class KeycloakIdentityAdminClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KeycloakAdminOptions Options => new()
    {
        BaseUrl = "https://auth.example.test",
        Realm = "platform",
        ClientId = "user-admin",
        ClientSecret = "injected-at-deploy-time",
    };

    private static KeycloakIdentityAdminClient Client(StubHandler handler)
        // 時計は実物で足りる（本テストはトークンの失効境界を測らない。測るなら偽の時計が要るが、
        // そのためだけに新しいパッケージを足さない —— 追加は CPM とライブラリ ratchet に効く）。
        => new(new StubFactory(handler, Options), Options, TimeProvider.System,
            NullLogger<KeycloakIdentityAdminClient>.Instance);

    private static string Token() => """{"access_token":"admin-token","expires_in":300}""";

    // 認証は client_credentials（機密クライアント）。以降の管理要求は Bearer を載せる。
    [Fact]
    public async Task It_obtains_a_client_credentials_token_and_bearers_the_admin_calls()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/roles", """[{"id":"1","name":"platform-admin"}]""");

        await Client(handler).ListAssignableRolesAsync(Ct);

        handler.Requests.Should().Contain(r =>
            r.Path.Contains("openid-connect/token") && r.Body!.Contains("grant_type=client_credentials"));
        handler.Requests.Should().Contain(r =>
            r.Path.EndsWith("/roles") && r.Authorization == "Bearer admin-token");
    }

    // 割当可能ロールの値域から Keycloak 既定の合成ロールを外す。
    // **出すと「default-roles-platform を利用者へ割り当てる」が画面から可能になる。**
    [Fact]
    public async Task Assignable_roles_exclude_keycloak_s_own_default_composites()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/roles", """
                [{"id":"1","name":"platform-admin"},
                 {"id":"2","name":"default-roles-platform"},
                 {"id":"3","name":"offline_access"},
                 {"id":"4","name":"uma_authorization"},
                 {"id":"5","name":"platform-operator"}]
                """);

        var roles = await Client(handler).ListAssignableRolesAsync(Ct);

        roles.Should().BeEquivalentTo(["platform-admin", "platform-operator"]);
    }

    // Keycloak のユーザー属性は多値である。判定側（BffScopeResolver）は 1 値しか読まないので
    // **先頭だけを取る**。表示名は姓名から組む。
    [Fact]
    public async Task It_maps_multi_valued_attributes_to_a_single_value()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users?briefRepresentation=false&max=1000", """
                [{"id":"u1","username":"tanaka.taro","firstName":"太郎","lastName":"田中",
                  "enabled":true,
                  "attributes":{"department":["finance"],"clearance":["internal","public"]}}]
                """)
            .Get("admin/realms/platform/users/u1/role-mappings/realm",
                """[{"id":"1","name":"platform-operator"},{"id":"2","name":"default-roles-platform"}]""");

        var users = await Client(handler).ListUsersAsync(Ct);

        users.Should().ContainSingle();
        users[0].DisplayName.Should().Be("田中 太郎");
        users[0].Attributes["clearance"].Should().Be("internal");
        users[0].Roles.Should().BeEquivalentTo(["platform-operator"]);
    }

    // 属性の差し替えは、契約の 1 キー 1 値を Keycloak の多値表現（単一要素の配列）へ写す。
    [Fact]
    public async Task Replacing_attributes_wraps_each_value_in_a_single_element_array()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","enabled":true,
                 "attributes":{"department":["hr"],"clearance":["internal"]}}
                """)
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

        var updated = await Client(handler).ReplaceAttributesAsync(
            "u1", new Dictionary<string, string> { ["department"] = "hr", ["clearance"] = "internal" }, Ct);

        updated.Should().NotBeNull();
        var put = handler.Requests.Single(r => r.Method == "PUT");
        using var body = JsonDocument.Parse(put.Body!);
        body.RootElement.GetProperty("attributes").GetProperty("department")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["hr"]);
    }

    // 「無効化→全セッション即時失効」の後半。Keycloak 側の失効がバックチャネルログアウトを起こす。
    [Fact]
    public async Task Revoking_sessions_posts_to_the_user_logout_endpoint()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Post("admin/realms/platform/users/u1/logout", "");

        (await Client(handler).RevokeSessionsAsync("u1", Ct)).Should().BeTrue();

        handler.Requests.Should().Contain(r => r.Method == "POST" && r.Path.EndsWith("/users/u1/logout"));
    }

    // 🔴 IADR-0329 (#1101): **`PUT /users/{id}` は部分更新ではない。**
    // `{"enabled": false}` だけを送ると `firstName` / `lastName` / `email` が実 Keycloak で
    // 実際に消えた（204 が返るので気付けない）。read-modify-write であることを固定する。
    [Fact]
    public async Task Updating_a_user_sends_the_whole_representation_not_a_patch()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","firstName":"太郎","lastName":"田中",
                 "email":"tanaka@example.test","enabled":true,
                 "requiredActions":["CONFIGURE_TOTP"],
                 "attributes":{"department":["hr"],"clearance":["internal"]},
                 "access":{"manage":true},"disableableCredentialTypes":[],
                 "userProfileMetadata":{"attributes":[]}}
                """)
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

        await Client(handler).SetEnabledAsync("u1", false, Ct);

        var put = handler.Requests.Single(r => r.Method == "PUT");
        using var body = JsonDocument.Parse(put.Body!);
        var root = body.RootElement;
        root.GetProperty("enabled").GetBoolean().Should().BeFalse("変更点は当然反映される");
        // **消えてはならないもの**（部分更新だと全部消える）。
        root.GetProperty("firstName").GetString().Should().Be("太郎");
        root.GetProperty("lastName").GetString().Should().Be("田中");
        root.GetProperty("email").GetString().Should().Be("tanaka@example.test");
        root.GetProperty("requiredActions").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["CONFIGURE_TOTP"], "MFA の要求アクションも消してはならない");
        root.GetProperty("attributes").GetProperty("clearance")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["internal"]);
        // **送り返してはならないもの**（サーバ計算の読み取り専用フィールド）。
        foreach (var computed in new[] { "access", "disableableCredentialTypes", "userProfileMetadata" })
            root.TryGetProperty(computed, out _).Should().BeFalse(
                "{0} はサーバが組み立てる派生値である", computed);
    }

    // 🔴 IADR-0329 (#1101): **黙って捨てられたら失敗にする。**
    // realm の user profile が unmanaged 属性の書き込みを許していないと、Keycloak は 204 を返して
    // ABAC 属性を捨てる。**200 を返して画面に「保存しました」と描かせない。**
    [Fact]
    public async Task Replacing_attributes_fails_loudly_when_keycloak_silently_drops_them()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            // 読み直しても **要求前の値のまま**（＝Keycloak が捨てた）。
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","enabled":true,
                 "attributes":{"department":["hr"],"clearance":["internal"]}}
                """)
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

        var act = async () => await Client(handler).ReplaceAttributesAsync(
            "u1", new Dictionary<string, string> { ["department"] = "hr", ["clearance"] = "restricted" }, Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("clearance").And.Contain("unmanagedAttributePolicy");
    }

    // 居ない利用者は null（端点が 404 へ写す）。**403 や 200 へ丸めない。**
    [Fact]
    public async Task An_unknown_user_maps_to_null()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Status("admin/realms/platform/users/ghost", HttpStatusCode.NotFound);

        (await Client(handler).SetEnabledAsync("ghost", false, Ct)).Should().BeNull();
    }

    // ---- 器 ----

    private sealed record Recorded(string Method, string Path, string? Body, string? Authorization);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.Ordinal);
        public List<Recorded> Requests { get; } = [];

        public StubHandler Get(string path, string body) => Register("GET", path, HttpStatusCode.OK, body);
        public StubHandler Post(string path, string body) => Register("POST", path, HttpStatusCode.OK, body);
        public StubHandler Put(string path, string body) => Register("PUT", path, HttpStatusCode.NoContent, body);
        public StubHandler Status(string path, HttpStatusCode status) => Register("GET", path, status, "");

        private StubHandler Register(string method, string path, HttpStatusCode status, string body)
        {
            _responses[$"{method} {path}"] = (status, body);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Recorded(request.Method.Method, path, body,
                request.Headers.Authorization?.ToString()));

            if (!_responses.TryGetValue($"{request.Method.Method} {path}", out var response))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };

            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(StubHandler handler, KeycloakAdminOptions options) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
        };
    }
}
