using AuthorizationService.Domain.Ports;
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
[Trait("TestKind", "Unit")]
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

    // ─────────────────────────────────────────────────────────────────────────
    // IADR-0385 (#1243): 集合値キー（tags / projects）は畳まずに連結する
    // ─────────────────────────────────────────────────────────────────────────
    //
    // 🔴 **これが #1243 の本体である。** 従前は一律に先頭 1 値へ畳んでおり、
    // `tags: ["sales","hr"]` の `hr` が静かに消えていた。部分集合判定（ADR-0062 決定 2）は
    // fail-closed 側へ倒れるが、**拒否理由が嘘になる**（「登録者が持つタグは 'sales' です」）。
    //
    // 🔴 **変異試験**: 連結を先頭 1 値へ戻すと本テストが落ちる。
    // **同じ変異で下の `単一値キーは従来どおり先頭だけを読む` は緑のまま通る**
    // （＝「全部連結する」実装と区別できている）。
    [Fact]
    public async Task 集合値キーの多値属性は畳まずに連結される()
    {
        var users = await Client(UsersHandler("""
            "attributes":{"tags":["sales","hr"],"projects":["alpha","beta"],
                          "clearance":["internal","public"]}
            """)).ListUsersAsync(Ct);

        users[0].Attributes["tags"].Should().Be("sales,hr");
        users[0].Attributes["projects"].Should().Be("alpha,beta");
    }

    // 🔴 **陰性対照（対で置く）。** 単一値キーまで連結すると `clearance` が
    // `"internal,public"` になり、階段ポリシーがどれもマッチしなくなる（deny 側だが静かに壊れる）。
    [Fact]
    public async Task 単一値キーは従来どおり先頭だけを読む()
    {
        var users = await Client(UsersHandler("""
            "attributes":{"tags":["sales","hr"],"clearance":["internal","public"],
                          "department":["finance","legal"]}
            """)).ListUsersAsync(Ct);

        users[0].Attributes["clearance"].Should().Be("internal");
        users[0].Attributes["department"].Should().Be("finance");
    }

    // もう 1 つの保存形（SC-17 の単一値書き込みが残した カンマ列）も**同じ線上表現**になる。
    [Fact]
    public async Task 単一値に入ったカンマ列も同じ線上表現になる()
    {
        var users = await Client(UsersHandler("""
            "attributes":{"tags":["sales,hr"]}
            """)).ListUsersAsync(Ct);

        users[0].Attributes["tags"].Should().Be("sales,hr");
    }

    // 空文字だけの多値は**キーごと落とす**（空文字を属性値として持たない）。
    [Fact]
    public async Task 空白だけの集合値キーは属性に現れない()
    {
        var users = await Client(UsersHandler("""
            "attributes":{"tags":["","  "],"clearance":["internal"]}
            """)).ListUsersAsync(Ct);

        users[0].Attributes.Should().NotContainKey("tags");
        users[0].Attributes["clearance"].Should().Be("internal");
    }

    // 書き込みの正準形は**多値配列**である（読み戻すと同じ線上表現に戻る）。
    [Fact]
    public async Task 集合値キーの差し替えは多値配列として送られる()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","enabled":true,
                 "attributes":{"tags":["sales","hr"],"clearance":["internal"]}}
                """)
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

        var updated = await Client(handler).ReplaceAttributesAsync(
            "u1", new Dictionary<string, string> { ["tags"] = "sales,hr", ["clearance"] = "internal" }, Ct);

        var put = handler.Requests.Single(r => r.Method == "PUT");
        using var body = JsonDocument.Parse(put.Body!);
        var attrs = body.RootElement.GetProperty("attributes");
        attrs.GetProperty("tags").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["sales", "hr"], o => o.WithStrictOrdering());
        // 陰性対照: 単一値キーは従来どおり単一要素の配列である。
        attrs.GetProperty("clearance").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["internal"]);
        // 読み戻しは同じ線上表現へ戻るので、黙って捨てられた判定（EnsureAttributesWereApplied）を通る。
        updated!.Attributes["tags"].Should().Be("sales,hr");
    }

    // 🔴 **正準化を通る値は「捨てられた」と誤検知してはならない。**
    // 集合値キーは分割して書き、連結して読み戻すので、`"sales hr"` と要求した値は
    // `"sales,hr"` として返る。序数で比べると **realm の設定不備でもないのに例外になる。**
    [Theory]
    [InlineData("sales hr")]
    [InlineData("sales, hr")]
    [InlineData("hr,sales")]
    public async Task 集合値キーは正準化後の集合として反映を突き合わせる(string requested)
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","enabled":true,
                 "attributes":{"tags":["sales","hr"]}}
                """)
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

        var updated = await Client(handler).ReplaceAttributesAsync(
            "u1", new Dictionary<string, string> { ["tags"] = requested }, Ct);

        updated!.Attributes["tags"].Should().Be("sales,hr");
    }

    // 🔴 **陰性対照（対で置く）。** 集合として比べても、**本当に捨てられた**ものは落とす ——
    // 緩めた結果「黙って捨てられた」を見逃すなら、緩めた意味が無い。
    [Fact]
    public async Task 集合値キーでも要素が落ちていれば失敗として上げる()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","enabled":true,
                 "attributes":{"tags":["sales"]}}
                """)
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

        var act = async () => await Client(handler).ReplaceAttributesAsync(
            "u1", new Dictionary<string, string> { ["tags"] = "sales,hr" }, Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("tags");
    }

    // 上の 5 本が使う名簿応答。属性の断片だけを差し替える。
    private static StubHandler UsersHandler(string attributesJson) => new StubHandler()
        .Post("realms/platform/protocol/openid-connect/token", Token())
        .Get("admin/realms/platform/users?briefRepresentation=false&max=1000",
            $$"""[{"id":"u1","username":"tanaka.taro","enabled":true,{{attributesJson}}}]""")
        .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

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

    // ---- 退職時の保持起点（FR-19, SC-19, 計画 ADR-0036 D-09, ADR-0082 決定 5, [[IADR-0428]] / #1392） ----

    // FR-19, SC-17, [[IADR-0428]]: 🔴 **ABAC 属性の差し替えで予約キー（保持起点）が消えない。**
    // `PUT /users/{id}` は送った表現で置き換えるので、**現在の表現から持ち越さないと消える** ——
    // 部門を 1 つ直しただけで退職時の窓の起点が失われる。
    // 陽性対照（差し替えたい ABAC 属性は要求どおりに載る）を同じ本文に置く。
    [Fact]
    public async Task Replacing_attributes_preserves_the_retention_anchor()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","enabled":true,
                 "attributes":{"department":["hr"],
                               "account_disabled_at":["2026-08-01T03:00:00Z"]}}
                """)
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

        await Client(handler).ReplaceAttributesAsync(
            "u1", new Dictionary<string, string> { ["department"] = "hr" }, Ct);

        var put = handler.Requests.Single(r => r.Method == "PUT");
        using var body = JsonDocument.Parse(put.Body!);
        var attrs = body.RootElement.GetProperty("attributes");
        attrs.GetProperty("department").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["hr"], "陽性対照: 差し替えたい属性は要求どおり載る");
        attrs.GetProperty("account_disabled_at").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["2026-08-01T03:00:00Z"]);
    }

    // FR-19, [[IADR-0428]]: 🔴 **要求側が予約キーを混ぜても採らない**（書き手は 1 つだけ）。
    // 採ると、SC-17 の差し替えから退職の起点を書き換えられる経路ができる。
    [Fact]
    public async Task Replacing_attributes_ignores_a_retention_anchor_sent_by_the_caller()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","enabled":true,
                 "attributes":{"department":["hr"],
                               "account_disabled_at":["2026-08-01T03:00:00Z"]}}
                """)
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

        await Client(handler).ReplaceAttributesAsync("u1", new Dictionary<string, string>
        {
            ["department"] = "hr",
            ["account_disabled_at"] = "1999-01-01T00:00:00Z",
        }, Ct);

        var put = handler.Requests.Single(r => r.Method == "PUT");
        using var body = JsonDocument.Parse(put.Body!);
        body.RootElement.GetProperty("attributes").GetProperty("account_disabled_at")
            .EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["2026-08-01T03:00:00Z"]);
    }

    // FR-19, SC-19, ADR-0082 決定 5, [[IADR-0428]]: 起点の書き込み。**他の属性を巻き添えにしない**
    //（`PUT` は置き換えなので、現在の表現から持ち越す）。
    [Fact]
    public async Task Setting_the_retention_anchor_writes_it_without_dropping_other_attributes()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","enabled":false,
                 "attributes":{"department":["hr"],"tags":["sales","hr"],
                               "account_disabled_at":["2026-08-01T03:00:00Z"]}}
                """)
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

        var updated = await Client(handler).SetRetentionAnchorAsync(
            "u1", "account_disabled_at", new DateTimeOffset(2026, 8, 1, 3, 0, 0, TimeSpan.Zero), Ct);

        updated.Should().NotBeNull();
        var put = handler.Requests.Single(r => r.Method == "PUT");
        using var body = JsonDocument.Parse(put.Body!);
        var attrs = body.RootElement.GetProperty("attributes");
        attrs.GetProperty("account_disabled_at").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["2026-08-01T03:00:00Z"]);
        attrs.GetProperty("department").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["hr"]);
        // 集合値キーは多値のまま持ち越す（単一値へ畳むと要素が落ちる）。
        attrs.GetProperty("tags").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["sales", "hr"]);
    }

    // FR-19, [[IADR-0428]]: 起点の消去（再有効化＝退職の取り消し）。
    // 🔴 **消えたことを読み直して確かめる。** 残ると復職者の資料が退職者と同じ期限で扱われる ——
    // 「書けなかった」より危険な向きなので、ここは fail-closed にする。
    // 本テストのスタブは読み直しでも同じ表現を返す（＝消えていない）ので、**例外になるのが正しい。**
    [Fact]
    public async Task Clearing_the_retention_anchor_omits_it_and_fails_closed_when_it_survives()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","enabled":true,
                 "attributes":{"department":["hr"],
                               "account_disabled_at":["2026-08-01T03:00:00Z"]}}
                """)
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

        var act = async () => await Client(handler).SetRetentionAnchorAsync(
            "u1", "account_disabled_at", null, Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("account_disabled_at");

        // 送った表現からは確かに落ちている（消去の要求そのものは正しく組めている）。
        var put = handler.Requests.Single(r => r.Method == "PUT");
        using var body = JsonDocument.Parse(put.Body!);
        var attrs = body.RootElement.GetProperty("attributes");
        attrs.TryGetProperty("account_disabled_at", out _).Should().BeFalse();
        attrs.GetProperty("department").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["hr"], "陽性対照: 消すのは起点だけである");
    }

    // 陰性対照: 居ない利用者への起点の書き込みは 404（例外にしない）。
    [Fact]
    public async Task Setting_the_retention_anchor_on_an_unknown_user_returns_null()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Status("admin/realms/platform/users/ghost", HttpStatusCode.NotFound);

        (await Client(handler).SetRetentionAnchorAsync("ghost", "account_disabled_at", null, Ct))
            .Should().BeNull();
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

    // ── FR-05, FR-16, NFR-09, SC-12, 計画 ADR-0088 決定 1・3, [[IADR-0413]] (#1333) ──
    // 名指しの 1 人を引く口。**列挙の上で絞る形の置き換えである。**

    // 🔴 T-1333-a: **`exact=true` が要る。** 既定の `username=` は前方一致であり、
    // `alice` を引くと `alice2` も返る。**別人の属性で ABAC を判定しうる。**
    [Fact]
    public async Task FindByUsername_asks_keycloak_for_an_exact_match_and_full_attributes()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users?username=alice&exact=true&briefRepresentation=false&max=2",
                """[{"id":"u1","username":"alice","enabled":true,"attributes":{"clearance":["internal"]}}]""");

        var user = await Client(handler).FindByUsernameAsync("alice", Ct);

        user!.Username.Should().Be("alice");
        user.Attributes["clearance"].Should().Be("internal");
        var asked = handler.Requests.Single(r => r.Path.Contains("/users?"));
        asked.Path.Should().Contain("exact=true", "前方一致だと別人が返り得る");
        asked.Path.Should().Contain("briefRepresentation=false", "false でないと attributes が返らない");
        asked.Authorization.Should().Be("Bearer admin-token");
    }

    // 🔴 T-1333-b: **列挙しない。** 従前の形（`ListUsersAsync` の上で絞る）は
    // ①判定ごとに全件列挙 ②`max=1000` の打ち切りで 1001 人目以降が「居ない」に見える、
    // という 2 つの欠陥を持っていた。**打ち切りのある口を引かないことをここで固定する。**
    [Fact]
    public async Task FindByUsername_never_enumerates_the_whole_directory()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users?username=alice&exact=true&briefRepresentation=false&max=2",
                """[{"id":"u1","username":"alice","enabled":true,"attributes":{}}]""");

        await Client(handler).FindByUsernameAsync("alice", Ct);

        handler.Requests.Should().NotContain(r => r.Path.Contains("max=1000"),
            "1000 件で打ち切られる列挙を引くと、1001 人目以降が deny になる");
        handler.Requests.Should().NotContain(r => r.Path.Contains("/role-mappings/"),
            "ロールは呼び出し元が読まない —— 引くと 1 人あたりの往復が増える");
    }

    // 🔴 T-1333-c: **候補はこちらでも絞る。** Keycloak の `exact` は realm の設定に依存するので、
    // 依存先の設定で照合規則が変わらないようにする（呼び出し元と同じ大小文字無視）。
    [Fact]
    public async Task FindByUsername_rejects_a_candidate_whose_name_is_not_the_one_asked_for()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users?username=alice&exact=true&briefRepresentation=false&max=2",
                """[{"id":"u2","username":"alice2","enabled":true,"attributes":{"clearance":["secret"]}}]""");

        (await Client(handler).FindByUsernameAsync("alice", Ct))
            .Should().BeNull("前方一致で紛れ込んだ別人を採ると、その人の属性で判定してしまう");
    }

    // 陽性対照（上の否定形と対）: 大小文字だけが違う候補は**同一人物として採る**（現行の照合規則）。
    [Fact]
    public async Task FindByUsername_matches_case_insensitively_like_the_existing_lookup()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users?username=Alice&exact=true&briefRepresentation=false&max=2",
                """[{"id":"u1","username":"alice","enabled":true,"attributes":{}}]""");

        (await Client(handler).FindByUsernameAsync("Alice", Ct))!.Username.Should().Be("alice");
    }

    // 「居ない」は null（応答）。**例外にしない** —— 呼び出し元が「引けなかった」と分けられなくなる。
    [Fact]
    public async Task FindByUsername_returns_null_when_nobody_matches()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users?username=ghost&exact=true&briefRepresentation=false&max=2", "[]");

        (await Client(handler).FindByUsernameAsync("ghost", Ct)).Should().BeNull();
    }

    // 🔴 **一意でなければ「引けなかった」へ倒す**（PR #1334 のレビュー指摘）。
    // `exact=true` でも realm の設定しだいで大小文字違いの 2 人が返り得る。
    // **先頭を採ると、どちらの属性で判定したかが応答順しだいになる** ——
    // 同じ要求が日によって違う判定を返す。**選ばずに落とす**（呼び出し元は deny へ倒れる）。
    [Fact]
    public async Task FindByUsername_refuses_to_choose_when_the_name_is_not_unique()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users?username=alice&exact=true&briefRepresentation=false&max=2",
                """
                [{"id":"u1","username":"alice","enabled":true,"attributes":{"clearance":["public"]}},
                 {"id":"u2","username":"Alice","enabled":true,"attributes":{"clearance":["secret"]}}]
                """);

        var act = async () => await Client(handler).FindByUsernameAsync("alice", Ct);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*一意でない*", "どちらの属性で ABAC を判定するかを応答順に委ねない");
    }

    // 🔴 集合値属性は線上表現のまま返る（[[IADR-0385]] 決定 2）——
    // 分解も再符号化もここではしない（評価器が交差判定を行う。[[IADR-0411]]）。
    [Fact]
    public async Task FindByUsername_joins_set_valued_attributes_like_the_listing_does()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users?username=alice&exact=true&briefRepresentation=false&max=2",
                """[{"id":"u1","username":"alice","enabled":true,"attributes":{"tags":["sales","hr"]}}]""");

        (await Client(handler).FindByUsernameAsync("alice", Ct))!.Attributes["tags"].Should().Be("sales,hr");
    }

    // ── FR-05, FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0036 D-03, ADR-0088 決定 1,
    //    ADR-0098 決定 1・3, [[IADR-0447]] (#1447): グループの 3 つの読み口 ──────────

    // 所属照会は `GET /users/{id}/groups`（**内部 ID** で引く・`briefRepresentation=true`）。
    // 🔴 **要求の形そのものを固定する** —— `briefRepresentation` を落とすと属性つきの広い像が
    // 返り、面を 3 項目へ閉じた意味が無くなる。
    [Fact]
    public async Task It_reads_the_memberships_of_a_user_by_internal_id()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users/u-1/groups?briefRepresentation=true", """
                [{"id":"g-1","name":"knowledge","path":"/teams/knowledge"},
                 {"id":"g-2","name":"finance","path":"/teams/finance"}]
                """);

        var groups = await Client(handler).GetUserGroupsAsync("u-1", Ct);

        groups.Select(g => g.Id).Should().Equal("g-1", "g-2");
        groups[0].Path.Should().Be("/teams/knowledge");
        handler.Requests.Should().Contain(r =>
            r.Path.EndsWith("users/u-1/groups?briefRepresentation=true")
            && r.Authorization == "Bearer admin-token");
    }

    // 🔴 **ID を持たない節は落とす**（判定と取り消しの鍵が無い像は画面でも使えない）。
    [Fact]
    public async Task Memberships_without_an_id_are_dropped()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users/u-1/groups?briefRepresentation=true", """
                [{"id":"","name":"broken","path":"/broken"},
                 {"id":"g-2","name":"finance","path":"/teams/finance"}]
                """);

        (await Client(handler).GetUserGroupsAsync("u-1", Ct))
            .Select(g => g.Id).Should().Equal("g-2");
    }

    // 🔴 **Keycloak 24 は search の結果を木のまま返す** —— 一致した子孫が祖先の `subGroups` に
    // 入って返る。**平坦化しないと子孫が 1 件も出ず、絞り直さないと一致していない祖先が混ざる。**
    // どちらもスタブでは自然には出ないので、実 Keycloak の応答の形を再現して固定する。
    [Fact]
    public async Task Group_search_flattens_subgroups_and_keeps_only_the_matching_names()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/groups?search=know&briefRepresentation=true", """
                [{"id":"g-teams","name":"teams","path":"/teams","subGroups":[
                    {"id":"g-know","name":"knowledge","path":"/teams/knowledge"},
                    {"id":"g-fin","name":"finance","path":"/teams/finance"}]}]
                """);

        var groups = await Client(handler).SearchGroupsAsync("know", 20, Ct);

        // 一致したのは子孫 1 件だけである（祖先 `teams` と兄弟 `finance` は名前が一致しない）。
        groups.Select(g => g.Path).Should().Equal("/teams/knowledge");
    }

    // 並びはパス順・打ち切りは**平坦化して絞ったあと**である（サーバ側の `max` は頂点に掛かる）。
    [Fact]
    public async Task Group_search_orders_by_path_and_applies_the_limit_after_flattening()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/groups?search=team&briefRepresentation=true", """
                [{"id":"g-z","name":"team-z","path":"/z/team-z"},
                 {"id":"g-a","name":"team-a","path":"/a/team-a","subGroups":[
                    {"id":"g-b","name":"team-b","path":"/a/team-a/team-b"}]}]
                """);

        var groups = await Client(handler).SearchGroupsAsync("team", 2, Ct);

        groups.Select(g => g.Path).Should().Equal("/a/team-a", "/a/team-a/team-b");
    }

    // 空のクエリ・0 件以下の上限では**往復しない**（後段を無駄に叩かない）。
    [Theory]
    [InlineData("", 20)]
    [InlineData("  ", 20)]
    [InlineData("know", 0)]
    public async Task Group_search_does_not_call_the_idp_for_a_meaningless_request(string query, int max)
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token());

        (await Client(handler).SearchGroupsAsync(query, max, Ct)).Should().BeEmpty();

        handler.Requests.Should().NotContain(r => r.Path.Contains("/groups"));
    }

    // 🔴 **1 判定ぶんの往復を実測して固定する**（計画 ADR-0098 フォローアップ 3 / [[IADR-0447]]）。
    //
    // `ScopeUserAttributeSource` が 1 判定で行うのは `FindByUsernameAsync` ＋ `GetUserGroupsAsync` で
    // あり、Keycloak Admin REST に対しては **GET 2 回**になる（#1447 の前は 1 回）。
    // 🔴 **admin トークンの取得は 1 回だけである**（`_token` を 2 つ目の呼び出しでも使い回す）——
    // 呼び出しごとに client_credentials を回す形へ退行すると、往復が 1 判定あたり 4 回になる。
    //
    // 🔴 **キャッシュは置かない**（`ADR-0088` 決定 1 を所属にも通す）。鮮度は IdP の読み取り
    // 一貫性そのものであり、そのかわり往復が判定ごとに掛かる。**その費用をここで見えるようにする。**
    [Fact]
    public async Task One_abac_decision_costs_two_admin_gets_and_one_token_fetch()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users?username=alice&exact=true&briefRepresentation=false&max=2", """
                [{"id":"u-1","username":"alice","firstName":"Alice","lastName":"A","enabled":true}]
                """)
            .Get("admin/realms/platform/users/u-1/groups?briefRepresentation=true", """
                [{"id":"g-1","name":"knowledge","path":"/teams/knowledge"}]
                """);
        var client = Client(handler);

        // 1 判定ぶん（属性の引き直し → 所属照会）。
        var user = await client.FindByUsernameAsync("alice", Ct);
        await client.GetUserGroupsAsync(user!.Id, Ct);

        handler.Requests.Count(r => r.Method == "GET").Should().Be(2,
            "属性の引き直し 1 ＋ 所属照会 1。所属を利用者ごと・グループごとに回す形へ退行させない");
        handler.Requests.Count(r => r.Path.Contains("openid-connect/token")).Should().Be(1,
            "admin トークンは使い回す（呼び出しごとに client_credentials を回さない）");
    }

    // 🔴 **ID 引き当ての 404 は落とす。エラーではない**（削除済みのグループへの共有は台帳に残る）。
    // 要求順を保つ（`Task.WhenAll` は入力順で返る）。
    [Fact]
    public async Task Resolving_group_ids_drops_the_missing_ones_and_keeps_the_request_order()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/groups/g-1", """
                {"id":"g-1","name":"knowledge","path":"/teams/knowledge"}
                """)
            .Get("admin/realms/platform/groups/g-2", """
                {"id":"g-2","name":"finance","path":"/teams/finance"}
                """);
        // `g-gone` は登録しない —— スタブは未登録のパスへ 404 を返す（＝削除済みのグループ）。

        var groups = await Client(handler).GetGroupsByIdsAsync(["g-1", "g-gone", "g-2"], Ct);

        groups.Select(g => g.Id).Should().Equal("g-1", "g-2");
    }

    // ── FR-05, SC-06, 計画 ADR-0115 決定 1・5, [[IADR-0472]] (#1557): フルパスでの引き当て ──

    // 陽性: `group-by-path` を 1 往復で引き、区切りの `/` を残してセグメントごとにエスケープする。
    [Fact]
    public async Task Finding_a_group_by_path_uses_group_by_path_and_escapes_each_segment()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/group-by-path/department/r%26d?briefRepresentation=true", """
                {"id":"g-rd","name":"r&d","path":"/department/r&d"}
                """);

        var group = await Client(handler).FindGroupByPathAsync("/department/r&d", Ct);

        group.Should().NotBeNull();
        group!.Id.Should().Be("g-rd");
        group.Path.Should().Be("/department/r&d");
    }

    // 陰性: 404 は「居ない」（null）。例外にしない。
    [Fact]
    public async Task Finding_a_missing_group_by_path_returns_null()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token());
        // 未登録のパスはスタブが 404 を返す。

        (await Client(handler).FindGroupByPathAsync("/department/finance", Ct)).Should().BeNull();
    }

    // 🔴 返ってきたパスが要求と序数一致しなければ null（IdP の格納層が大小文字を畳んでも、値域は大小文字を区別する）。
    [Fact]
    public async Task Finding_a_group_by_path_rejects_a_case_folded_match()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/group-by-path/department/Sales?briefRepresentation=true", """
                {"id":"g-s","name":"sales","path":"/department/sales"}
                """);

        (await Client(handler).FindGroupByPathAsync("/department/Sales", Ct)).Should().BeNull();
    }

    // #1557 監査: 🔴 応答が path を持たないときは**推測しない**（名前から `/名前` を組み立てて比べない）。例外 ＝ 502 側。
    [Fact]
    public async Task Finding_a_group_by_path_without_a_path_in_the_response_fails_instead_of_guessing()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/group-by-path/department/sales?briefRepresentation=true", """
                {"id":"g-s","name":"sales"}
                """);

        var act = async () => await Client(handler).FindGroupByPathAsync("/department/sales", Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("path");
    }

    // 🔴 404 以外の失敗は例外（＝ gRPC 面で status。「値域の外」と混ぜない）。
    [Fact]
    public async Task Finding_a_group_by_path_throws_on_non_404_failure()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Status("admin/realms/platform/group-by-path/department/sales?briefRepresentation=true",
                HttpStatusCode.Forbidden);

        var act = async () => await Client(handler).FindGroupByPathAsync("/department/sales", Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ── FR-05, FR-09, SC-17, 計画 ADR-0115 決定 3, [[IADR-0473]] (#1573): 部門の同期が使う読み書き ──

    // 🔴 所属者は**最後のページまで**読む（1 ページで止めると 101 人目以降が黙って対象から落ちる）。
    [Fact]
    public async Task Listing_group_members_reads_every_page_with_attributes()
    {
        var page1 = "[" + string.Join(",", Enumerable.Range(0, KeycloakIdentityAdminClient.PageSize).Select(i =>
            $$$"""{"id":"u{{{i}}}","username":"user{{{i}}}","enabled":true,"attributes":{"department":["sales"]}}""")) + "]";
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/groups/g-sales/members?briefRepresentation=false&first=0&max=100", page1)
            .Get("admin/realms/platform/groups/g-sales/members?briefRepresentation=false&first=100&max=100", """
                [{"id":"u-last","username":"last","enabled":true,"attributes":{"department":["hr"]}}]
                """);

        var members = await Client(handler).ListGroupMembersAsync("g-sales", Ct);

        members.Should().HaveCount(KeycloakIdentityAdminClient.PageSize + 1);
        members[^1].Id.Should().Be("u-last");
        members[^1].Attributes["department"].Should().Be("hr", "属性つき（briefRepresentation=false）で引く");
        handler.Requests.Should().NotContain(r => r.Path.Contains("role-mappings"), "ロールは引かない");
    }

    [Fact]
    public async Task Listing_sub_groups_returns_direct_children_with_paths()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/groups/g-dept/children?briefRepresentation=true&first=0&max=100", """
                [{"id":"g-eng","name":"engineering","path":"/department/engineering"},
                 {"id":"g-sales","name":"sales","path":"/department/sales"}]
                """);

        var children = await Client(handler).ListSubGroupsAsync("g-dept", Ct);

        children.Select(g => g.Path).Should().Equal("/department/engineering", "/department/sales");
    }

    // 🔴 `department` 1 キーだけを差し替え、**他の属性は多値のまま持ち越す**（全置換で畳まない）。
    // 本スタブは読み直しでも古い値（hr）を返す ＝ 捨てられた形なので、**例外になるのが正しい**（fail-closed）。
    [Fact]
    public async Task Setting_the_department_writes_only_that_key_and_fails_closed_when_it_is_dropped()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","enabled":true,
                 "attributes":{"department":["hr"],"tags":["sales","hr"],"clearance":["internal","public"]}}
                """)
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");

        var observed = Observed("u1", enabled: true,
            ("department", "hr"), ("tags", "sales,hr"), ("clearance", "internal"));
        var act = async () => await Client(handler).SetDepartmentAttributeAsync("u1", "sales", observed, Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("department");
        var put = handler.Requests.Single(r => r.Method == "PUT");
        using var body = JsonDocument.Parse(put.Body!);
        var attrs = body.RootElement.GetProperty("attributes");
        attrs.GetProperty("department").EnumerateArray().Select(e => e.GetString()).Should().Equal("sales");
        attrs.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).Should().Equal("sales", "hr");
        attrs.GetProperty("clearance").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(["internal", "public"], "単一値キーの 2 値目も落とさない（全置換で畳まない）");
    }

    [Fact]
    public async Task Setting_the_department_returns_the_reloaded_user_when_applied_and_null_when_missing()
    {
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1", """
                {"id":"u1","username":"tanaka.taro","enabled":true,"attributes":{"department":["sales"]}}
                """)
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]")
            .Status("admin/realms/platform/users/ghost", HttpStatusCode.NotFound);

        var applied = await Client(handler).SetDepartmentAttributeAsync(
            "u1", "sales", Observed("u1", enabled: true, ("department", "hr")), Ct);
        applied.Outcome.Should().Be(DepartmentWriteOutcome.Applied);
        applied.User!.Attributes["department"].Should().Be("sales");
        (await Client(handler).SetDepartmentAttributeAsync("ghost", "sales", Observed("ghost", enabled: true), Ct))
            .Outcome.Should().Be(DepartmentWriteOutcome.NotFound);
    }

    // #1573 監査: 🔴 計画の読み取りの後に SC-17 の無効化（enabled=false ＋ 保持起点）が入っていたら、
    // **PUT しない**（古い表現で上書きして無効化を取り消さない）。
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Setting_the_department_is_skipped_when_the_user_changed_since_the_read(
        bool enabledNow, bool anchorAdded)
    {
        var anchor = anchorAdded ? ",\"account_disabled_at\":[\"2026-09-26T00:00:00Z\"]" : "";
        var current = "{\"id\":\"u1\",\"username\":\"t\",\"enabled\":" + (enabledNow ? "true" : "false")
            + ",\"attributes\":{\"department\":[\"hr\"]" + anchor + "}}";
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users/u1", current);

        var result = await Client(handler).SetDepartmentAttributeAsync(
            "u1", "sales", Observed("u1", enabled: true, ("department", "hr")), Ct);

        result.Outcome.Should().Be(DepartmentWriteOutcome.Changed);
        handler.Requests.Should().NotContain(r => r.Method == "PUT", "変わっていたら書かない");
    }

    // #1573 監査: 子グループも最後のページまで読む。
    [Fact]
    public async Task Listing_sub_groups_reads_every_page()
    {
        var page1 = "[" + string.Join(",", Enumerable.Range(0, KeycloakIdentityAdminClient.PageSize).Select(i =>
            "{\"id\":\"g" + i + "\",\"name\":\"d" + i + "\",\"path\":\"/department/d" + i + "\"}")) + "]";
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/groups/g-dept/children?briefRepresentation=true&first=0&max=100", page1)
            .Get("admin/realms/platform/groups/g-dept/children?briefRepresentation=true&first=100&max=100",
                "[{\"id\":\"g-last\",\"name\":\"last\",\"path\":\"/department/last\"}]");

        var children = await Client(handler).ListSubGroupsAsync("g-dept", Ct);

        children.Should().HaveCount(KeycloakIdentityAdminClient.PageSize + 1);
        children[^1].Path.Should().Be("/department/last");
    }

    // ── FR-05, FR-09, SC-17, 計画 ADR-0116 決定 2, [[IADR-0473]] (#1609): 全利用者の列挙と部門の消去 ──

    // T-56 の土台: 🔴 全利用者は**最後のページまで**読み、読み切れたら Complete = true。サービスアカウントは返さない。
    [Fact]
    public async Task Listing_all_users_reads_every_page_and_skips_service_accounts()
    {
        var page1 = "[" + string.Join(",", Enumerable.Range(0, KeycloakIdentityAdminClient.PageSize).Select(i =>
            $$$"""{"id":"u{{{i}}}","username":"user{{{i}}}","enabled":true,"attributes":{"department":["sales"]}}""")) + "]";
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users?briefRepresentation=false&first=0&max=100", page1)
            .Get("admin/realms/platform/users?briefRepresentation=false&first=100&max=100", """
                [{"id":"u-last","username":"last","enabled":true,"attributes":{"department":["hr"]}},
                 {"id":"sa-1","username":"service-account-abac-seeder","enabled":true,"serviceAccountClientId":"abac-seeder",
                  "attributes":{"department":["engineering"]}},
                 {"id":"sa-2","username":"odd-name","enabled":true,"serviceAccountClientId":"odd","attributes":{}}]
                """);

        var result = await Client(handler).ListAllUsersAsync(Ct);

        result.Complete.Should().BeTrue();
        result.Users.Should().HaveCount(KeycloakIdentityAdminClient.PageSize + 1);
        result.Users[^1].Attributes["department"].Should().Be("hr", "属性つき（briefRepresentation=false）で引く");
        result.Users.Select(u => u.Id).Should().NotContain(["sa-1", "sa-2"], "サービスアカウントは返さない");
        handler.Requests.Should().NotContain(r => r.Path.Contains("role-mappings"), "ロールは引かない");
    }

    // T-56: 🔴 ページの途中の失敗は**例外**（部分的な結果を返さない）。本文が JSON の null のページも例外である
    // （空のページと読むと、そこで列挙が終わったことになる）。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Listing_all_users_throws_when_a_page_cannot_be_read(bool nullBody)
    {
        var page1 = "[" + string.Join(",", Enumerable.Range(0, KeycloakIdentityAdminClient.PageSize).Select(i =>
            $$$"""{"id":"u{{{i}}}","username":"user{{{i}}}","enabled":true}""")) + "]";
        var handler = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users?briefRepresentation=false&first=0&max=100", page1);
        if (nullBody) handler.Get("admin/realms/platform/users?briefRepresentation=false&first=100&max=100", "null");
        // nullBody=false のときは 2 ページ目が未登録 ＝ 404（ページの失敗）。

        var act = async () => await Client(handler).ListAllUsersAsync(Ct);

        await act.Should().ThrowAsync<Exception>();
    }

    // T-55: `department` 1 キーだけを消し、他の属性は多値のまま持ち越す。読み直して消えていれば Applied。
    [Fact]
    public async Task Clearing_the_department_removes_only_that_key()
    {
        var handler = new SequencedHandler(
            """{"id":"u1","username":"t","enabled":true,"attributes":{"department":["hr"],"tags":["sales","hr"],"clearance":["internal","public"]}}""",
            """{"id":"u1","username":"t","enabled":true,"attributes":{"tags":["sales","hr"],"clearance":["internal","public"]}}""");

        var result = await new KeycloakIdentityAdminClient(new SequencedFactory(handler, Options), Options,
                TimeProvider.System, NullLogger<KeycloakIdentityAdminClient>.Instance)
            .ClearDepartmentAttributeAsync("u1",
                Observed("u1", enabled: true, ("department", "hr"), ("tags", "sales,hr"), ("clearance", "internal")), Ct);

        result.Outcome.Should().Be(DepartmentWriteOutcome.Applied);
        using var body = JsonDocument.Parse(handler.PutBody!);
        var attrs = body.RootElement.GetProperty("attributes");
        attrs.TryGetProperty("department", out _).Should().BeFalse("department だけを消す");
        attrs.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).Should().Equal("sales", "hr");
        attrs.GetProperty("clearance").EnumerateArray().Select(e => e.GetString()).Should().Equal("internal", "public");
    }

    // T-55: 🔴 読み直して残っていれば**例外**（消したつもりで残さない）。読み取り後に変わっていれば PUT しない。
    [Fact]
    public async Task Clearing_the_department_fails_closed_when_it_remains_and_skips_when_changed()
    {
        var stays = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Put("admin/realms/platform/users/u1", "")
            .Get("admin/realms/platform/users/u1", """{"id":"u1","username":"t","enabled":true,"attributes":{"department":["hr"]}}""")
            .Get("admin/realms/platform/users/u1/role-mappings/realm", "[]");
        var act = async () => await Client(stays).ClearDepartmentAttributeAsync(
            "u1", Observed("u1", enabled: true, ("department", "hr")), Ct);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("department");

        var changed = new StubHandler()
            .Post("realms/platform/protocol/openid-connect/token", Token())
            .Get("admin/realms/platform/users/u1", """{"id":"u1","username":"t","enabled":false,"attributes":{"department":["hr"]}}""");
        (await Client(changed).ClearDepartmentAttributeAsync("u1", Observed("u1", enabled: true, ("department", "hr")), Ct))
            .Outcome.Should().Be(DepartmentWriteOutcome.Changed);
        changed.Requests.Should().NotContain(r => r.Method == "PUT");
    }

    // 1 回目の GET は書く前の像、2 回目以降は読み直しの像を返すスタブ（消去の反映を確かめるため）。
    private sealed class SequencedHandler(string before, string after) : HttpMessageHandler
    {
        private int _userGets;
        public string? PutBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            string body;
            if (path.Contains("openid-connect/token")) body = Token();
            else if (path.EndsWith("/role-mappings/realm", StringComparison.Ordinal)) body = "[]";
            else if (request.Method == HttpMethod.Put)
            {
                PutBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            else body = Interlocked.Increment(ref _userGets) == 1 ? before : after;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SequencedFactory(SequencedHandler handler, KeycloakAdminOptions options) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
        };
    }

    private static IdentityUser Observed(string id, bool enabled, params (string Key, string Value)[] attributes)
        => new(id, "t", "t", enabled, [], attributes.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal));

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
            // #1447: `GetGroupsByIdsAsync` は ID ごとに**並列で**引くので、観測点を lock で守る。
            lock (Requests)
            {
                Requests.Add(new Recorded(request.Method.Method, path, body,
                    request.Headers.Authorization?.ToString()));
            }

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
