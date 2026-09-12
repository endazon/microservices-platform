using AuthorizationService.Features.Groups.Lookup.LookupGroups;
using AuthorizationService.Features.Groups.Lookup.ResolveGroups;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using System.Net;
using System.Net.Http.Json;

namespace AuthorizationService.Tests.Features.Groups.Lookup;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1・3, ADR-0100 決定 1・フォローアップ 2,
// [[IADR-0447]] / [[IADR-0449]] (#1447):
// 共有先に指定するグループの検索（`/authz/groups/lookup`）と表示名の引き当て（`/authz/groups/resolve`）。
//
// 固定する性質は 4 つで、**どれも片側だけでは測れない**ので陽性対照と対で置く
// （`UserLookupEndpointTests` と**同じ形**にしてある —— 2 つの口の作法が違うと画面に分岐が生える）。
//   1. 🔴 **人の主体だけが到達できる**（受け入れ基準 6。サービスアカウントは 403・未認証は 401）。
//   2. **入力の下限・上限**（`q` 2 文字以上・`limit` 1〜50・`ids` 1〜100。受け入れ基準 7）。
//   3. **無い ID は応答から落ちる**（エラーではない。削除済みのグループへの共有が台帳に残る）。
//   4. **面に出るのは 3 項目だけ**（所属者・属性を運ばない）。
//
// 身元プロバイダは in-memory の偽物（`TestWebApplicationFactory` が宣言する）。固定のグループ木
// （`/teams`・`/teams/knowledge`・`/teams/finance`・`/department`・`/department/engineering`）を持つ。
[Trait("TestKind", "Integration")]
public class GroupLookupEndpointTests(TestWebApplicationFactory factory)
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

    private HttpClient AsServiceAccount(params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UsernameHeader, "service-account-platform");
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.RolesHeader,
            string.Join(',', roles.Length == 0 ? [PlatformAuthPolicies.ServiceRole] : roles));
        return client;
    }

    private HttpClient Anonymous()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");
        return client;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string path)
        => path.EndsWith("/resolve", StringComparison.Ordinal)
            ? client.PostAsJsonAsync(path, new ResolveGroupsRequest(["g-knowledge"]), Ct)
            : client.GetAsync(path, Ct);

    // ── 1. 🔴 人の主体だけ（受け入れ基準 6）─────────────────────────────

    [Theory]
    [InlineData("/authz/groups/lookup?q=know")]
    [InlineData("/authz/groups/resolve")]
    public async Task A_human_subject_reaches_both_endpoints(string path)
        => (await Send(AsRegularUser(), path)).StatusCode.Should().Be(HttpStatusCode.OK);

    // 管理者でも同じく通る（ロールで**絞っていない**ことの確認。ADR-0098 決定 1）。
    [Fact]
    public async Task An_admin_can_use_the_same_endpoints()
        => (await factory.CreateClient().GetAsync("/authz/groups/lookup?q=know", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    [Theory]
    [InlineData("/authz/groups/lookup?q=know")]
    [InlineData("/authz/groups/resolve")]
    public async Task A_service_account_is_forbidden(string path)
        => (await Send(AsServiceAccount(), path)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

    // 🔴 **ロールを持つサービスアカウントでも 403**（ロールではなく主体の種別で分ける）。
    [Theory]
    [InlineData("/authz/groups/lookup?q=know")]
    [InlineData("/authz/groups/resolve")]
    public async Task A_service_account_holding_the_admin_role_is_still_forbidden(string path)
        => (await Send(AsServiceAccount(PlatformAuthPolicies.AdminRole), path)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

    [Theory]
    [InlineData("/authz/groups/lookup?q=know")]
    [InlineData("/authz/groups/resolve")]
    public async Task An_anonymous_request_is_unauthorized(string path)
        => (await Send(Anonymous(), path)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

    // 群の認可メタデータを経路表で固定する（`InteractiveUser` ただ 1 つ・AdminOnly ではない）。
    [Fact]
    public void The_group_lookup_endpoints_require_the_interactive_user_policy()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (
                Pattern: "/" + (e.RoutePattern.RawText ?? string.Empty).Trim('/'),
                Authorize: e.Metadata.GetOrderedMetadata<IAuthorizeData>()))
            .ToList();

        foreach (var pattern in new[] { "/authz/groups/lookup", "/authz/groups/resolve" })
        {
            var endpoint = endpoints.Should().ContainSingle(e => e.Pattern == pattern).Subject;
            endpoint.Authorize.Should().OnlyContain(
                a => a.Policy == PlatformAuthPolicies.InteractiveUser,
                $"{pattern} は人の主体だけを通す（ロール＝AdminOnly は掛けない）");
        }

        // 🔴 **書き込みの口は無い**（グループ木は管理者が Keycloak で作る。ADR-0098 決定 3）。
        endpoints.Should().NotContain(e => e.Pattern == "/authz/groups");
    }

    // ── 2. 入力の下限・上限（受け入れ基準 7）───────────────────────────

    [Theory]
    [InlineData("k")]
    [InlineData(" ")]
    [InlineData("")]
    public async Task Lookup_rejects_a_query_shorter_than_two_characters(string q)
        => (await AsRegularUser().GetAsync($"/authz/groups/lookup?q={Uri.EscapeDataString(q)}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Lookup_rejects_a_limit_outside_the_allowed_range()
    {
        var client = AsRegularUser();

        (await client.GetAsync($"/authz/groups/lookup?q=te&limit={LookupGroupsEndpoint.MaxLimit + 1}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/authz/groups/lookup?q=te&limit=0", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 陽性対照: 上限そのものは通る（境界を 1 つずらした実装を検出する）。
        (await client.GetAsync($"/authz/groups/lookup?q=te&limit={LookupGroupsEndpoint.MaxLimit}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 **木は平坦化されて返り、並びはパス順である**（表示名順ではない）。
    // `in` に一致するのは別の枝にある 2 つ（`/teams/finance` と `/department/engineering`）——
    // **親を伴わずに子が返る**ことと、枝を跨いでパスで並ぶことが同時に見える。
    [Fact]
    public async Task Lookup_flattens_the_tree_and_orders_by_path()
    {
        var found = await AsRegularUser().GetFromJsonAsync<List<GroupSummaryDto>>(
            "/authz/groups/lookup?q=in", Ct);

        found!.Select(g => g.Path).Should().Equal("/department/engineering", "/teams/finance");
    }

    [Fact]
    public async Task Lookup_honours_the_limit()
    {
        var client = AsRegularUser();

        // 陽性対照を先に置く: 1 つしか一致しない語は 1 件だけ返る（＝常に全件ではない）。
        (await client.GetFromJsonAsync<List<GroupSummaryDto>>("/authz/groups/lookup?q=teams", Ct))
            .Should().ContainSingle().Which.Id.Should().Be("g-teams");

        // 絞ると打ち切られる（上の 2 件が 1 件になる）。
        (await client.GetFromJsonAsync<List<GroupSummaryDto>>(
            "/authz/groups/lookup?q=in&limit=1", Ct))
            .Should().ContainSingle().Which.Path.Should().Be("/department/engineering");
    }

    [Fact]
    public async Task Resolve_rejects_an_empty_or_oversized_request()
    {
        var client = AsRegularUser();

        (await client.PostAsJsonAsync("/authz/groups/resolve",
            new ResolveGroupsRequest([]), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var tooMany = Enumerable.Range(0, ResolveGroupsEndpoint.MaxIds + 1)
            .Select(i => $"g-{i}").ToList();
        (await client.PostAsJsonAsync("/authz/groups/resolve",
            new ResolveGroupsRequest(tooMany), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 🔴 空の要素は 400 である（黙って落とさない —— 呼び出し側の誤りが消える）。
        (await client.PostAsJsonAsync("/authz/groups/resolve",
            new ResolveGroupsRequest(["g-knowledge", "  "]), Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 陽性対照: 上限ちょうどは通る。
        var atLimit = Enumerable.Range(0, ResolveGroupsEndpoint.MaxIds)
            .Select(i => $"g-{i}").ToList();
        (await client.PostAsJsonAsync("/authz/groups/resolve",
            new ResolveGroupsRequest(atLimit), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── 3. 🔴 無い ID は落ちる（エラーではない）────────────────────────

    [Fact]
    public async Task Resolve_drops_unknown_ids_without_failing()
    {
        var resp = await AsRegularUser().PostAsJsonAsync("/authz/groups/resolve",
            new ResolveGroupsRequest(["g-knowledge", "g-deleted", "g-finance"]), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var groups = await resp.Content.ReadFromJsonAsync<List<GroupSummaryDto>>(Ct);
        // **要求順を保つ**（画面が受け取る順は台帳の順＝付与順のままである）。
        groups!.Select(g => g.Id).Should().Equal("g-knowledge", "g-finance");
    }

    // 陽性対照（3 の対）: すべて無い ID でも 200 ＋ 空配列である（404 にしない）。
    [Fact]
    public async Task Resolve_returns_an_empty_list_when_nothing_is_found()
    {
        var resp = await AsRegularUser().PostAsJsonAsync("/authz/groups/resolve",
            new ResolveGroupsRequest(["g-gone"]), Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<List<GroupSummaryDto>>(Ct)).Should().BeEmpty();
    }

    // ── 4. 🔴 面に出るのは 3 項目だけ ────────────────────────────────

    [Fact]
    public async Task The_surface_carries_only_id_display_name_and_path()
    {
        var client = AsRegularUser();

        var lookupJson = await client.GetStringAsync("/authz/groups/lookup?q=know", Ct);
        var resolveJson = await (await client.PostAsJsonAsync("/authz/groups/resolve",
            new ResolveGroupsRequest(["g-knowledge"]), Ct)).Content.ReadAsStringAsync(Ct);

        foreach (var json in new[] { lookupJson, resolveJson })
        {
            // 陽性対照: 3 項目は在る。
            json.Should().Contain("\"id\"").And.Contain("displayName").And.Contain("path");
            // 本題: 所属者・属性を運ぶ項目は 1 つも無い。
            json.Should().NotContain("members").And.NotContain("attributes")
                .And.NotContain("roles").And.NotContain("subGroups");
        }
    }
}
