using System.Net;
using System.Net.Http.Json;
using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Tests.Features.Authz.ResolveScope;

// FR-05, NFR-09, UC-05, 計画 ADR-0004, ADR-0084 決定 1, ADR-0086 決定 4, **ADR-0088 決定 1・2・4**,
// [[IADR-0401]] 決定 2, [[IADR-0411]], [[IADR-0413]] (#1333):
// 🔴 **呼び出し元が本文で主張した利用者属性は、判定に用いられない。**
//
// これは #1333 の**核心**である。従前 `AuthzScope/Resolve` は主張をそのまま評価しており、
// **`platform-service` を持つサービスが 1 つ侵害されれば任意の利用者の権限スコープが取れた**
// （`ADR-0086` 決定 4 が「受け入れたリスク」として記録し、planning#564 の裁定が是正を決めた構造）。
//
// 🔴 **陽性と陰性を対で置く。** 「主張しても通らない」だけでは、**評価器が常に deny を返す実装**でも
// 緑になる。「IdP 側の属性なら通る」を同じ器で示して初めて、
// **判定が起きたうえで主張が捨てられている**ことが言える。
[Trait("TestKind", "Integration")]
public class ClaimedUserAttributesAreIgnoredTests : IClassFixture<TestWebApplicationFactory>
{
    private const string Key = "clearance-1333";
    private readonly TestWebApplicationFactory _factory;
    private readonly string _policyName = $"claim-probe-{Guid.NewGuid():N}";

    public ClaimedUserAttributesAreIgnoredTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        SeedPolicy();
    }

    // `clearance-1333 = internal` を持つ利用者にだけ `confidentiality ∈ {internal}` を許す。
    private void SeedPolicy()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        if (db.Policies.Any(p => p.Name == _policyName)) return;
        db.Policies.Add(AbacPolicy.Create(
            _policyName,
            PolicyAction.Read,
            new Dictionary<string, List<string>> { [Key] = ["internal"] },
            new Dictionary<string, List<string>> { ["confidentiality"] = ["internal"] }));
        db.SaveChanges();
    }

    private Task<HttpResponseMessage> ResolveAsync(string userId, Dictionary<string, string> claimed) =>
        _factory.CreateServiceCallerClient().PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest(userId, claimed), TestContext.Current.CancellationToken);

    private static async Task<AccessScopeResponse> BodyOf(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<AccessScopeResponse>(TestContext.Current.CancellationToken))!;

    // 🔴 T-01（陽性対照）: **IdP 側の属性なら通る。** 判定そのものは生きている。
    [Fact]
    public async Task The_attributes_the_idp_holds_decide_the_scope()
    {
        var user = $"real-{Guid.NewGuid():N}"[..20];
        _factory.Identity.Attributes[user] = new Dictionary<string, string> { [Key] = "internal" };

        // **本文は空である** —— それでも通ることが「主張ではなく IdP が根拠である」ことを示す。
        var resp = await ResolveAsync(user, []);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var scope = await BodyOf(resp);
        scope.Granted.Should().BeTrue("IdP が持つ属性で判定される");
        scope.Branches.Should().Contain(b => b.Name == _policyName);
    }

    // 🔴 T-02（本 issue の核心）: **偽の属性を主張しても通らない。**
    [Fact]
    public async Task A_claimed_attribute_does_not_grant_anything()
    {
        var user = $"liar-{Guid.NewGuid():N}"[..20];
        _factory.Identity.Attributes[user] = new Dictionary<string, string> { [Key] = "public" };

        // 呼び出し元が「この利用者は internal だ」と主張する。**IdP は public と言っている。**
        var resp = await ResolveAsync(user, new Dictionary<string, string> { [Key] = "internal" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyOf(resp)).Branches.Should().NotContain(b => b.Name == _policyName,
            "主張が評価に使われていたら、ここで internal のポリシーにマッチしてしまう");
    }

    // 🔴 T-03: **引き直しが実際に走っている**ことの直接の観測。
    // T-02 だけだと「評価器がこのポリシーを見ていないだけ」でも緑になり得る。
    [Fact]
    public async Task The_service_looks_the_user_up_in_the_identity_provider()
    {
        var user = $"probe-{Guid.NewGuid():N}"[..20];
        _factory.Identity.Attributes[user] = new Dictionary<string, string>();

        await ResolveAsync(user, []);

        _factory.Identity.LookedUp.Should().Contain(user);
    }

    // 🔴 T-04: **「居ない」は応答である。** 200 ＋ `granted=false` で返る（エラーにしない）。
    [Fact]
    public async Task An_unknown_user_is_denied_by_a_normal_response()
    {
        var user = $"ghost-{Guid.NewGuid():N}"[..20];
        _factory.Identity.Unknown.Add(user);

        var resp = await ResolveAsync(user, new Dictionary<string, string> { [Key] = "internal" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyOf(resp)).Granted.Should().BeFalse();
    }

    // 🔴 T-05: **「引けなかった」は status である**（`ADR-0088` 決定 1）。
    // 200 ＋ `granted=false` にすると、**後段の停止が「その利用者に権限が無い」として記録される**。
    // 🔴 それでも **fail-closed は保たれる** —— 既知の呼び出し元 4 つはいずれも非 2xx を deny へ縮退する。
    [Fact]
    public async Task An_identity_provider_outage_is_a_status_not_a_denial()
    {
        var user = $"outage-{Guid.NewGuid():N}"[..20];
        _factory.Identity.Attributes[user] = new Dictionary<string, string> { [Key] = "internal" };
        _factory.Identity.Failure = new HttpRequestException("Keycloak へ届かない");

        try
        {
            var resp = await ResolveAsync(user, []);

            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
                "『引けなかった』と『権限が無い』は別の事実である");
        }
        finally
        {
            _factory.Identity.Failure = null;
        }
    }

    // 🔴 T-06: **REST 面にも認可が掛かっている**（`ADR-0088` 決定 2。決定 1 の着地の条件）。
    // 資格情報が無ければ 401 —— 従前この端点は**認可を 1 つも掛けていなかった**。
    [Fact]
    public async Task The_rest_face_rejects_a_caller_without_credentials()
    {
        // TestAuthHandler は**ヘッダが無いと管理者**を名乗るので、
        // 「資格情報なし」は**空でないロールを持たない主体**として作る（下の T-07 と対）。
        var resp = await _factory.CreateClient().PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest("anyone", []), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "認証はできても platform-service を持たない主体は通さない");
    }

    // 🔴 T-07: **管理者の利用者トークンでも通らない**（confused deputy の防止。[[IADR-0379]] 決定 4）。
    // これはサービスが呼ぶ面であり、**利用者の資格では開かない**。gRPC 面と同じ水準である。
    [Fact]
    public async Task The_rest_face_rejects_an_administrators_user_token()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, PlatformAuthPolicies.AdminRole);

        var resp = await client.PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest("anyone", []), TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 陽性対照（T-06 / T-07 と対）: `platform-service` を持つ主体は通る。
    // これが無いと、上の 2 つは**端点が壊れていて常に 403**でも緑になる。
    [Fact]
    public async Task The_rest_face_accepts_a_service_caller()
    {
        var resp = await ResolveAsync($"ok-{Guid.NewGuid():N}"[..20], []);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
