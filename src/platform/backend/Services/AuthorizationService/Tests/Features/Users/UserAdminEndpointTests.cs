using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;
using AuthorizationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace AuthorizationService.Tests.Features.Users;

// FR-05, FR-09, UC-05, SC-17, ADR-0026 (#452): 利用者アカウント管理エンドポイントの結合テスト。
// 身元プロバイダは in-memory の偽物（TestWebApplicationFactory が宣言する）。
//
// ※ InMemory DB はクラスごとに分離されるため（TestWebApplicationFactory の注記）、
//   本クラスは自分で利用者スコープの属性辞書を投入してから割当を試す。
[Trait("TestKind", "Integration")]
public class UserAdminEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private HttpClient Client => factory.CreateClient();

    private record UserDto(string Id, string Username, string DisplayName, bool Enabled,
        List<string> Roles, Dictionary<string, string> Attributes);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SeedUserDictionaryAsync()
    {
        // 既に投入済みなら 400（キー重複）が返る。冪等にしたいので状態を先に見る。
        var existing = await Client.GetFromJsonAsync<List<Dictionary<string, object>>>(
            "/authz/attributes", Ct);
        if (existing is { Count: > 0 }) return;

        foreach (var body in new object[]
        {
            new { Key = "department", Label = "所属部門", AllowedValues = new[] { "engineering", "finance", "hr" }, Required = false, Scope = "user" },
            new { Key = "clearance", Label = "取扱可能区分", AllowedValues = new[] { "public", "internal", "confidential", "restricted" }, Required = false, Scope = "user" },
            new { Key = "tags", Label = "タグ", AllowedValues = new[] { "management", "finance" }, Required = false, Scope = "user" },
        })
        {
            (await Client.PostAsJsonAsync("/authz/attributes", body, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }
    }

    // ---- 一覧・値域 ----

    // SC-17 主要素 1: 利用者一覧（部門・ロール・ABAC 属性・状態）。
    [Fact]
    public async Task ListUsers_returns_roles_attributes_and_state()
    {
        var users = await Client.GetFromJsonAsync<List<UserDto>>("/authz/users", Ct);

        users.Should().NotBeNull().And.NotBeEmpty();
        users!.Should().Contain(u => u.Roles.Count > 0);
        users.Should().Contain(u => u.Attributes.ContainsKey("department"));
        // 退職者（人事連携で自動無効化された利用者）が偽物の初期データに居る。
        users.Should().Contain(u => !u.Enabled);
    }

    // SC-17 入力規則「定義済みロールのみ」の値域。**画面はこれを引く。**
    [Fact]
    public async Task AssignableRoles_are_served_from_the_identity_provider()
    {
        var roles = await Client.GetFromJsonAsync<List<string>>("/authz/users/assignable-roles", Ct);

        roles.Should().NotBeNull().And.Contain("platform-admin");
        // Keycloak 既定の合成ロールを人の割当対象に出さない。
        roles!.Should().NotContain(r => r.StartsWith("default-roles-", StringComparison.Ordinal));
    }

    // ---- ロール割当 ----

    // SC-17: 複数併任は通る（陽性対照）。
    [Fact]
    public async Task ReplaceRoles_accepts_multiple_assignable_roles()
    {
        var res = await Client.PutAsJsonAsync("/authz/users/u-tanaka/roles",
            new { Roles = new[] { "platform-admin", "platform-operator" } }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await res.Content.ReadFromJsonAsync<UserDto>(Ct);
        user!.Roles.Should().BeEquivalentTo("platform-admin", "platform-operator");
    }

    // SC-17: ロール割当は必須（空集合は 400）。
    [Fact]
    public async Task ReplaceRoles_rejects_an_empty_assignment()
        => (await Client.PutAsJsonAsync("/authz/users/u-sato/roles",
                new { Roles = Array.Empty<string>() }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

    // SC-17: 定義済みロールのみ。
    [Fact]
    public async Task ReplaceRoles_rejects_an_undefined_role()
        => (await Client.PutAsJsonAsync("/authz/users/u-sato/roles",
                new { Roles = new[] { "realm-management" } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task ReplaceRoles_on_an_unknown_user_is_404()
        => (await Client.PutAsJsonAsync("/authz/users/u-nobody/roles",
                new { Roles = new[] { "platform-admin" } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

    // ---- ABAC 属性割当 ----

    // SC-17: 部門・機密区分上限は必須／タグは任意（陽性対照つき）。
    [Fact]
    public async Task ReplaceAttributes_accepts_required_pair_without_optional_tag()
    {
        await SeedUserDictionaryAsync();

        var res = await Client.PutAsJsonAsync("/authz/users/u-suzuki/attributes",
            new
            {
                Attributes = new Dictionary<string, string>
                {
                    ["department"] = "finance",
                    ["clearance"] = "confidential",
                }
            }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await res.Content.ReadFromJsonAsync<UserDto>(Ct);
        user!.Attributes["department"].Should().Be("finance");
        user.Attributes.Should().NotContainKey("tags");
    }

    [Fact]
    public async Task ReplaceAttributes_rejects_a_missing_required_attribute()
    {
        await SeedUserDictionaryAsync();

        (await Client.PutAsJsonAsync("/authz/users/u-suzuki/attributes",
                new { Attributes = new Dictionary<string, string> { ["department"] = "finance" } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReplaceAttributes_rejects_a_value_outside_the_dictionary()
    {
        await SeedUserDictionaryAsync();

        (await Client.PutAsJsonAsync("/authz/users/u-suzuki/attributes",
                new
                {
                    Attributes = new Dictionary<string, string>
                    {
                        ["department"] = "finance",
                        ["clearance"] = "top-secret",
                    }
                }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- 無効化・再有効化 ----

    // SC-17 アクション:「無効化→全セッション即時失効」。
    [Fact]
    public async Task Disable_turns_the_account_off_and_enable_turns_it_back_on()
    {
        var disabled = await Client.PostAsync("/authz/users/u-tanaka/disable", null, Ct);
        disabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await disabled.Content.ReadFromJsonAsync<UserDto>(Ct))!.Enabled.Should().BeFalse();

        var enabled = await Client.PostAsync("/authz/users/u-tanaka/enable", null, Ct);
        enabled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await enabled.Content.ReadFromJsonAsync<UserDto>(Ct))!.Enabled.Should().BeTrue();
    }

    // 🔴 05_screens §SC-17 アクション:「無効化→**全セッション即時失効**」。
    // **無効化しただけでは満たされない。** 失効の要求まで測らないと、失効を落とす変異が素通りする。
    [Fact]
    public async Task Disable_also_revokes_every_session_of_that_user()
    {
        // #1333: 引き直しの装飾を挟んだので、DI から取れるのは装飾のほうである。
        // **観測点は包まれた本物の偽物のほうが持つ。**
        _ = factory.Services.GetRequiredService<IIdentityAdminClient>();
        var identity = (InMemoryIdentityAdminClient)factory.Identity.Inner;
        var before = identity.RevokedSessionRequests.Count;

        (await Client.PostAsync("/authz/users/u-sato/disable", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        identity.RevokedSessionRequests.Skip(before).Should().Contain("u-sato");
    }

    // 陽性対照: **再有効化ではセッションを復活させない**（そもそも失効を要求しない）。
    // 「常に失効させる」実装でもこの対がないと緑になる。
    [Fact]
    public async Task Enable_does_not_revoke_anything()
    {
        // #1333: 引き直しの装飾を挟んだので、DI から取れるのは装飾のほうである。
        // **観測点は包まれた本物の偽物のほうが持つ。**
        _ = factory.Services.GetRequiredService<IIdentityAdminClient>();
        var identity = (InMemoryIdentityAdminClient)factory.Identity.Inner;
        var before = identity.RevokedSessionRequests.Count;

        (await Client.PostAsync("/authz/users/u-suzuki/enable", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        identity.RevokedSessionRequests.Should().HaveCount(before);
    }

    [Fact]
    public async Task Disable_on_an_unknown_user_is_404()
        => (await Client.PostAsync("/authz/users/u-nobody/disable", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

    // ---- 退職時の保持起点（FR-19, SC-19, 計画 ADR-0036 D-09, ADR-0082 決定 5, [[IADR-0428]] / #1392） ----

    // FR-19, SC-19, ADR-0082 決定 5: 🔴 **無効化した日を起点として刻む。**
    // D-09 の「退職日から 30 日間」の退職日を持つ経路は他に無い（Keycloak の `enabled` に日付は無い）。
    //
    // 🔴 **再無効化で起点は動かない**（冪等）。動くと、失効を確実にするために押し直すたびに窓が延びる。
    // 🔴 **再有効化で起点は消える** —— 残ると復職者の資料が退職者と同じ期限で扱われる。
    [Fact]
    public async Task Disable_stamps_the_retention_anchor_idempotently_and_enable_clears_it()
    {
        var identity = Identity();

        (await Client.PostAsync("/authz/users/u-tanaka/disable", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var stamped = await AnchorOfAsync(identity, "u-tanaka");
        stamped.Should().NotBeNull("無効化日を刻まなければ、窓の起点はどこにも残らない");
        RetentionAnchorPolicy
            .Resolve(new Dictionary<string, string>
            {
                [RetentionAnchorAttributes.AccountDisabledAtKey] = stamped!,
            }, new RetentionAnchorOptions())
            .State.Should().Be(RetentionAnchorState.Supplied, "刻んだ値は起点として読めなければならない");

        (await Client.PostAsync("/authz/users/u-tanaka/disable", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await AnchorOfAsync(identity, "u-tanaka")).Should().Be(stamped, "再無効化で窓を延ばさない");

        (await Client.PostAsync("/authz/users/u-tanaka/enable", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await AnchorOfAsync(identity, "u-tanaka")).Should().BeNull("再有効化は退職の取り消しである");
    }

    // FR-19, SC-17, [[IADR-0428]]: 🔴 **起点は SC-17 の応答へ出さない。**
    // 出すと画面が下書きへ写して差し替え要求へ送り返し、辞書に無いキーとして 400 になる
    // （＝無効化済み利用者の属性編集が壊れる）。**「持っていない」ではなく「出していない」ことを測る**
    // ので、生の身元（偽の IdP）と応答 DTO の両方を見る。
    [Fact]
    public async Task The_retention_anchor_is_never_exposed_on_the_screen_contract()
    {
        await SeedUserDictionaryAsync();
        var identity = Identity();

        var disabled = await Client.PostAsync("/authz/users/u-suzuki/disable", null, Ct);
        var dto = await disabled.Content.ReadFromJsonAsync<UserDto>(Ct);

        (await AnchorOfAsync(identity, "u-suzuki")).Should().NotBeNull("陽性対照: 起点は身元の側に在る");
        dto!.Attributes.Should().NotContainKey(RetentionAnchorAttributes.AccountDisabledAtKey);
        dto.Attributes.Should().ContainKey("department", "ABAC 属性まで落としたら出しすぎである");

        // 画面が実際に送る形（辞書に定義された 2 キーだけ）で差し替えても通り、**起点は消えない。**
        var replaced = await Client.PutAsJsonAsync("/authz/users/u-suzuki/attributes",
            new
            {
                Attributes = new Dictionary<string, string>
                {
                    ["department"] = "finance",
                    ["clearance"] = "confidential",
                }
            }, Ct);
        replaced.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AnchorOfAsync(identity, "u-suzuki"))
            .Should().NotBeNull("部門を 1 つ直しただけで退職時の窓の起点が消えてはならない");

        // 後片付け（本クラスは器を共有する）。再有効化で起点も消える。
        (await Client.PostAsync("/authz/users/u-suzuki/enable", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 包まれた本物の偽物（観測点）。#1333 で引き直しの装飾が挟まったので DI からは取れない。
    private IIdentityAdminClient Identity()
    {
        _ = factory.Services.GetRequiredService<IIdentityAdminClient>();
        return factory.Identity.Inner;
    }

    private static async Task<string?> AnchorOfAsync(IIdentityAdminClient identity, string userId)
    {
        var user = (await identity.ListUsersAsync(Ct)).Single(u => u.Id == userId);
        return user.Attributes.TryGetValue(RetentionAnchorAttributes.AccountDisabledAtKey, out var value)
            ? value
            : null;
    }

    // ---- アクセス制御（システム管理者ロール限定） ----

    [Fact]
    public async Task Non_admin_roles_are_refused()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");

        (await client.GetAsync("/authz/users", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/authz/users/u-tanaka/disable", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- 🔴 新規作成の口が無いこと（計画 05_screens §SC-17） ----

    [Fact]
    public void The_route_table_has_no_user_creation_endpoint()
    {
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            // `MapGet("")` を群へ足すと RawText は `/authz/users/`（末尾スラッシュつき）になる。
            // ルーティングは同一視するので、比較の前に落として揃える。
            .Select(e => (
                Pattern: "/" + (e.RoutePattern.RawText ?? string.Empty).Trim('/'),
                Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []))
            .ToList();

        // 陽性対照: 割当の口は在る（この一覧が空でないことを先に確かめる）。
        routes.Should().Contain(r => r.Pattern == "/authz/users" && r.Methods.Contains("GET"));

        // 本題: 作成に相当する動詞が /authz/users の直下に無い。
        routes.Should().NotContain(
            r => r.Pattern == "/authz/users" && (r.Methods.Contains("POST") || r.Methods.Contains("PUT")),
            "計画 05_screens §SC-17 は本画面からの新規作成を禁じている");
    }
}
