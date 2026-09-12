using AuthorizationService.Features.Users.Lookup.LookupUsers;
using AuthorizationService.Features.Users.Lookup.ResolveUsers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using System.Net;
using System.Net.Http.Json;

namespace AuthorizationService.Tests.Features.Users.Lookup;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0445]] (#1445):
// 共有先に指定する利用者の検索（`/authz/users/lookup`）と表示名の引き当て（`/authz/users/resolve`）。
//
// 固定する性質は 4 つで、**どれも片側だけでは測れない**ので陽性対照と対で置く。
//   1. **一般利用者で通る**（共有は所有者の操作である）。**同時に、AdminOnly の管理面は
//      一般利用者で 403 のままである** —— 群を取り違えて `/authz/users` へ足すと、
//      ここが緑のまま管理面が開く（またはこちらが 403 になる）。
//   2. **入力の下限・上限**（`q` 2 文字以上・`limit` 1〜50・`usernames` 1〜100）。
//   3. 🔴 **無効化済みの扱いが 2 つの口で非対称である**（lookup には出ず・resolve には出る）。
//   4. **面に出るのは 3 項目だけ**（ロール・ABAC 属性・内部 ID を運ばない）。
//
// 身元プロバイダは in-memory の偽物（`TestWebApplicationFactory` が宣言する）。初期データに
// **退職者（`takahashi.jiro` / 無効）** が居り、3 の非対称はこれで測る。
[Trait("TestKind", "Integration")]
public class UserLookupEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 管理者ロールを持たない一般利用者（共有先を選ぶのはこの主体である）。
    private HttpClient AsRegularUser()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "platform-operator");
        return client;
    }

    // ヘッダ無し＝既定で `platform-admin`（`TestAuthHandler` の注記）。
    private HttpClient AsAdmin() => factory.CreateClient();

    // ── 1. 一般利用者で通る（＋ AdminOnly の管理面は 403 のまま）────────────────

    [Fact]
    public async Task Lookup_and_resolve_are_open_to_a_regular_user()
    {
        var client = AsRegularUser();

        var lookup = await client.GetAsync("/authz/users/lookup?q=tanaka", Ct);
        lookup.StatusCode.Should().Be(HttpStatusCode.OK);
        (await lookup.Content.ReadFromJsonAsync<List<UserSummaryDto>>(Ct))
            .Should().Contain(u => u.Username == "tanaka.taro");

        var resolve = await client.PostAsJsonAsync("/authz/users/resolve",
            new ResolveUsersRequest(["tanaka.taro"]), Ct);
        resolve.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resolve.Content.ReadFromJsonAsync<List<UserSummaryDto>>(Ct))
            .Should().ContainSingle().Which.DisplayName.Should().Be("田中 太郎");
    }

    // 管理者でも同じく通る（ロールで**絞っていない**ことの確認）。
    [Fact]
    public async Task An_admin_can_use_the_same_endpoints()
        => (await AsAdmin().GetAsync("/authz/users/lookup?q=sato", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    // 🔴 **陽性対照（逆向き）: 管理面の認可は変わっていない。**
    // 群を 1 つにまとめる変更（どちらの向きでも）はここで赤くなる。
    [Fact]
    public async Task The_admin_only_user_management_stays_forbidden_for_a_regular_user()
    {
        var client = AsRegularUser();

        (await client.GetAsync("/authz/users", Ct)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/authz/users/u-tanaka/disable", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    // **認証は必須である。** テストの認証ハンドラは常に認証を通すため（無認証を作れない）、
    // 「群に認可メタデータが在り、かつ AdminOnly ではない」ことを経路表で固定する。
    [Fact]
    public void The_lookup_group_requires_authentication_but_not_the_admin_policy()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (
                Pattern: "/" + (e.RoutePattern.RawText ?? string.Empty).Trim('/'),
                Authorize: e.Metadata.GetOrderedMetadata<IAuthorizeData>()))
            .ToList();

        foreach (var pattern in new[] { "/authz/users/lookup", "/authz/users/resolve" })
        {
            var endpoint = endpoints.Should().ContainSingle(e => e.Pattern == pattern).Subject;
            endpoint.Authorize.Should().NotBeEmpty($"{pattern} は認証を要求する");
            endpoint.Authorize.Should().OnlyContain(a => string.IsNullOrEmpty(a.Policy),
                $"{pattern} はロールを要求しない（AdminOnly を掛けない）");
        }

        // 陽性対照: 同じ接頭辞の管理面には AdminOnly が掛かっている。
        endpoints.Should().Contain(e => e.Pattern == "/authz/users"
            && e.Authorize.Any(a => a.Policy == PlatformAuthPolicies.AdminOnly));
    }

    // ── 2. 入力の下限・上限 ─────────────────────────────────────

    [Theory]
    [InlineData("t")]
    [InlineData(" ")]
    [InlineData("")]
    public async Task Lookup_rejects_a_query_shorter_than_two_characters(string q)
        => (await AsRegularUser().GetAsync($"/authz/users/lookup?q={Uri.EscapeDataString(q)}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Lookup_rejects_a_limit_outside_the_allowed_range()
    {
        var client = AsRegularUser();

        (await client.GetAsync($"/authz/users/lookup?q=ta&limit={LookupUsersEndpoint.MaxLimit + 1}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/authz/users/lookup?q=ta&limit=0", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 陽性対照: 上限そのものは通る（境界を 1 つずらした実装を検出する）。
        (await client.GetAsync($"/authz/users/lookup?q=ta&limit={LookupUsersEndpoint.MaxLimit}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lookup_honours_the_limit()
    {
        var client = AsRegularUser();

        // 偽物の初期データで `ro` に一致するのは 3 人（tanaka.taro / suzuki.ichiro /
        // **takahashi.jiro＝無効**）。陽性対照を先に置く: 絞らなければ**有効な 2 人**が返る
        // （無効化済みが混ざらないことも、ここで同時に測れる）。
        var all = await client.GetFromJsonAsync<List<UserSummaryDto>>(
            "/authz/users/lookup?q=ro", Ct);
        all!.Select(u => u.Username).Should().BeEquivalentTo("tanaka.taro", "suzuki.ichiro");

        (await client.GetFromJsonAsync<List<UserSummaryDto>>(
            "/authz/users/lookup?q=ro&limit=1", Ct)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Resolve_rejects_an_empty_or_oversized_request()
    {
        var client = AsRegularUser();

        (await client.PostAsJsonAsync("/authz/users/resolve",
            new ResolveUsersRequest([]), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var tooMany = Enumerable.Range(0, ResolveUsersEndpoint.MaxUsernames + 1)
            .Select(i => $"user{i}").ToList();
        (await client.PostAsJsonAsync("/authz/users/resolve",
            new ResolveUsersRequest(tooMany), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 🔴 空の要素は 400 である（黙って落とさない —— 呼び出し側の誤りが消える）。
        (await client.PostAsJsonAsync("/authz/users/resolve",
            new ResolveUsersRequest(["tanaka.taro", "  "]), Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 陽性対照: 上限ちょうどは通る。
        var atLimit = Enumerable.Range(0, ResolveUsersEndpoint.MaxUsernames)
            .Select(i => $"user{i}").ToList();
        (await client.PostAsJsonAsync("/authz/users/resolve",
            new ResolveUsersRequest(atLimit), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── 3. 🔴 無効化済みの扱いは 2 つの口で非対称である ───────────────────

    [Fact]
    public async Task A_disabled_user_is_hidden_from_lookup_but_still_resolves()
    {
        var client = AsRegularUser();

        // 陰性: 退職者は候補に出ない（新たな共有先に指定させない）。
        var candidates = await client.GetFromJsonAsync<List<UserSummaryDto>>(
            "/authz/users/lookup?q=takahashi", Ct);
        candidates.Should().BeEmpty();

        // 陽性対照（同じ主体・同じ名前）: 既存の共有先としては引ける。**`enabled=false` で区別する。**
        var resolved = await client.PostAsJsonAsync("/authz/users/resolve",
            new ResolveUsersRequest(["takahashi.jiro"]), Ct);
        var users = await resolved.Content.ReadFromJsonAsync<List<UserSummaryDto>>(Ct);
        users.Should().ContainSingle().Which.Enabled.Should().BeFalse();

        // 陽性対照（lookup 側）: 有効な利用者は候補に出る（「常に空」の実装ではない）。
        (await client.GetFromJsonAsync<List<UserSummaryDto>>(
            "/authz/users/lookup?q=suzuki", Ct)).Should().NotBeEmpty();
    }

    // 居ない名前は**応答から落ちる**（エラーではない）。見つかったものだけが返る。
    [Fact]
    public async Task Resolve_drops_unknown_usernames_without_failing()
    {
        var resolved = await AsRegularUser().PostAsJsonAsync("/authz/users/resolve",
            new ResolveUsersRequest(["tanaka.taro", "ghost.user", "sato.hanako"]), Ct);

        resolved.StatusCode.Should().Be(HttpStatusCode.OK);
        var users = await resolved.Content.ReadFromJsonAsync<List<UserSummaryDto>>(Ct);
        users!.Select(u => u.Username).Should().Equal("tanaka.taro", "sato.hanako");
    }

    // ── 4. 🔴 面に出るのは 3 項目だけ（ロール・ABAC 属性・内部 ID を運ばない）──────

    [Fact]
    public async Task The_surface_carries_only_username_display_name_and_enabled()
    {
        var client = AsRegularUser();

        var lookupJson = await client.GetStringAsync("/authz/users/lookup?q=suzuki", Ct);
        var resolveJson = await (await client.PostAsJsonAsync("/authz/users/resolve",
            new ResolveUsersRequest(["suzuki.ichiro"]), Ct)).Content.ReadAsStringAsync(Ct);

        foreach (var json in new[] { lookupJson, resolveJson })
        {
            // 陽性対照: 3 項目は在る。
            json.Should().Contain("username").And.Contain("displayName").And.Contain("enabled");
            // 本題: 管理面の項目は 1 つも無い。
            json.Should().NotContain("roles").And.NotContain("attributes")
                .And.NotContain("clearance").And.NotContain("department")
                .And.NotContain("\"id\"", "内部 ID（IdP の利用者 ID）を出さない");
        }
    }
}
