using System.Net;
using System.Net.Http.Json;
using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Tests.Features.Authz.ResolveScope;

// FR-05, FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0036 D-03・D-06, ADR-0088 決定 1,
// ADR-0098 決定 1・フォローアップ 1, [[IADR-0447]] (#1447):
// 🔴 **`${current_groups}` は IdP の所属照会で束縛される**（REST 面の受け入れ基準 1・2）。
//
// 従前、束縛変数は `${current_user}` だけで `${current_groups}` は実装全体に 0 件であり、
// **グループ共有は台帳に入るが誰にも何も許可しなかった**（#1447）。
//
// 🔴 **陽性と陰性を対で置く。** 「所属が効く」だけでは *常に全グループを許す* 実装でも緑になり、
// 「所属が無いと効かない」だけでは *常に deny* の実装でも緑になる。
//
// 🔴 **供給元はトークンではなく IdP である**（`ADR-0088` 決定 1）。本文にもトークンにも
// グループを載せる口が無いことを、器の側（`TestIdentityDirectory.Groups`）で示す ——
// 所属を書ける場所が IdP の像ただ 1 つであることが、この試験の形そのものである。
[Trait("TestKind", "Integration")]
public class CurrentGroupsBindingTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Key = "clearance-1447";
    private readonly TestWebApplicationFactory _factory;
    private readonly string _policyName = $"shared-with-probe-{Guid.NewGuid():N}";

    public CurrentGroupsBindingTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        SeedPolicy();
    }

    // 計画 07_abac-attribute-model §動的束縛の判定規則
    // `doc.shared_with ∩ ({${current_user}} ∪ ${current_groups}) ≠ ∅` を写したポリシー 1 本。
    // 🔴 **利用者条件に専用のキーを使う** —— 器（`TestWebApplicationFactory`）は他のクラスと
    // DB を共有しないが、**同じクラス内の他の利用者にまで当たらないように**絞る。
    private void SeedPolicy()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        if (db.Policies.Any(p => p.Name == _policyName)) return;
        db.Policies.Add(AbacPolicy.Create(
            _policyName,
            PolicyAction.Read,
            new Dictionary<string, List<string>> { [Key] = ["internal"] },
            new Dictionary<string, List<string>>
            {
                ["shared_with"] = ["${current_user}", "${current_groups}"],
            }));
        db.SaveChanges();
    }

    private string NewUser(string label, string[]? groups)
    {
        var user = $"{label}-{Guid.NewGuid():N}"[..20];
        _factory.Identity.Attributes[user] = new Dictionary<string, string> { [Key] = "internal" };
        if (groups is not null) _factory.Identity.Groups[user] = groups;
        return user;
    }

    private Task<HttpResponseMessage> ResolveAsync(string userId) =>
        _factory.CreateServiceCallerClient().PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest(userId, []), TestContext.Current.CancellationToken);

    private async Task<List<string>> SharedWithValuesOf(string userId)
    {
        var resp = await ResolveAsync(userId);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var scope = (await resp.Content.ReadFromJsonAsync<AccessScopeResponse>(
            TestContext.Current.CancellationToken))!;
        return scope.Branches!.Single(b => b.Name == _policyName)
            .Filters.Single(f => f.Key == DocumentAttributeEncoding.SharedWithKey)
            .AllowedValues;
    }

    // 🔴 T-1447-a（陽性）: 所属が `shared_with` の許可値へ**展開される**（主体と同居する）。
    [Fact]
    public async Task The_scope_binds_the_memberships_the_identity_provider_holds()
    {
        var user = NewUser("member", ["g-knowledge", "g-finance"]);

        (await SharedWithValuesOf(user)).Should()
            .BeEquivalentTo(user, "g-knowledge", "g-finance");
    }

    // 🔴 T-1447-b（陰性対照・同じポリシー）: 所属が無ければ `${current_user}` だけが残る。
    // **他人のグループ共有へ到達しない。**
    [Fact]
    public async Task A_user_without_memberships_only_binds_itself()
    {
        var user = NewUser("loner", []);

        (await SharedWithValuesOf(user)).Should().BeEquivalentTo([user]);
    }

    // 🔴 T-1447-c: **所属照会が実際に走っている**ことの直接の観測。
    // T-1447-a だけだと「器が属性と一緒に所属を返しているだけ」でも緑になり得る。
    [Fact]
    public async Task The_service_asks_the_identity_provider_for_the_memberships()
    {
        var user = NewUser("probe", ["g-knowledge"]);

        await ResolveAsync(user);

        // 🔴 鍵は**内部 ID**である（利用者名では引けない。Keycloak の所属照会の形）。
        _factory.Identity.GroupsLookedUp.Should().Contain(
            TestIdentityDirectory.StubIdPrefix + user,
            "所属は利用者名ではなく IdP の内部 ID で引く");
    }

    // 🔴 T-1447-d（受け入れ基準 2）: **所属照会の失敗は 503 である**（`ADR-0088` 決定 1）。
    // deny（200 ＋ `granted=false`）に畳むと、**IdP の不調が「そのグループに共有されていない」
    // として記録される**。属性の失敗とまったく同じ扱いにする。
    // 🔴 それでも fail-closed は保たれる —— 呼び出し元は非 2xx を deny へ縮退する。
    [Fact]
    public async Task A_membership_lookup_outage_is_a_status_not_a_denial()
    {
        var user = NewUser("outage", ["g-knowledge"]);
        // **属性は引ける**（`Failure` ではなく `GroupFailure` を立てる）。所属だけが引けない。
        _factory.Identity.GroupFailure = new HttpRequestException("Keycloak の所属照会へ届かない");
        try
        {
            var resp = await ResolveAsync(user);

            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
                "『所属を引けなかった』と『そのグループに共有されていない』は別の事実である");
        }
        finally
        {
            _factory.Identity.GroupFailure = null;
        }
    }

    // 陽性対照（T-1447-d の対）: 同じ利用者・同じ器で、失敗を解けば 200 に戻る。
    // これが無いと T-1447-d は「常に 503」の実装でも緑になる。
    [Fact]
    public async Task The_same_user_resolves_normally_once_the_lookup_recovers()
    {
        var user = NewUser("recovered", ["g-knowledge"]);

        (await ResolveAsync(user)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 T-1447-f（鮮度と費用の実測。計画 ADR-0098 フォローアップ 3 / [[IADR-0447]]）:
    // **1 判定あたりの IdP 往復は 2 つ**である（属性の引き直し 1 ＋ 所属照会 1）。
    //
    // 🔴 **キャッシュは置かない**（`ADR-0088` 決定 1「判定ごとに引き直す」を所属にも通す）ので、
    // 鮮度は IdP の読み取り一貫性そのものである。**そのかわり往復は判定ごとに掛かる。**
    //
    // 🔴 **この数を固定するのは N+1 の再発を止めるためである。** 従前、名指しの 1 人が要る経路は
    // 「全件列挙 ＋ 人数分のロール照会」を回していた（[[IADR-0413]] / #1333 が是正した）——
    // 所属照会を利用者ごとに回す形（あるいは所属の名前解決を 1 件ずつ引く形）を足すと、
    // **同じ壊れ方が別の軸で戻る。**
    [Fact]
    public async Task One_decision_costs_exactly_two_identity_provider_round_trips()
    {
        var user = NewUser("cost", ["g-knowledge", "g-finance"]);
        var lookedUpBefore = _factory.Identity.LookedUp.Count;
        var groupsLookedUpBefore = _factory.Identity.GroupsLookedUp.Count;

        await ResolveAsync(user);

        _factory.Identity.LookedUp.Count.Should().Be(lookedUpBefore + 1,
            "属性の引き直しは 1 判定あたり 1 回である（列挙して絞る形へ戻していない）");
        // 🔴 **所属が 2 件でも照会は 1 回である**（グループごとに引かない）。
        _factory.Identity.GroupsLookedUp.Count.Should().Be(groupsLookedUpBefore + 1,
            "所属照会は 1 判定あたり 1 回である（所属の件数に比例させない）");
    }

    // 🔴 T-1447-e: **本文でグループを主張する口が無い**（`ADR-0088` 決定 1 の帰結）。
    // 契約 `AccessScopeRequest` は利用者属性しか運ばないため、`shared_with` を利用者属性として
    // 主張しても束縛値は変わらない —— **所属の出所は IdP ただ 1 つである。**
    [Fact]
    public async Task Claimed_attributes_cannot_add_a_group_to_the_binding()
    {
        var user = NewUser("liar", ["g-knowledge"]);

        var resp = await _factory.CreateServiceCallerClient().PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest(user, new Dictionary<string, string>
            {
                [Key] = "internal",
                // 「自分は g-secret にも居る」という主張。
                [DocumentAttributeEncoding.SharedWithKey] = "g-secret",
            }),
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var scope = (await resp.Content.ReadFromJsonAsync<AccessScopeResponse>(
            TestContext.Current.CancellationToken))!;
        scope.Branches!.Single(b => b.Name == _policyName)
            .Filters.Single(f => f.Key == DocumentAttributeEncoding.SharedWithKey)
            .AllowedValues.Should().NotContain("g-secret")
            .And.BeEquivalentTo(user, "g-knowledge");
    }
}
