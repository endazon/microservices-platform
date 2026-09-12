using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// FR-19, SC-19 主要素 3, 計画 ADR-0098 決定 1, ADR-0100 決定 1・フォローアップ 2,
// [[IADR-0401]] 決定 2, [[IADR-0449]] (#1447): 名簿の読み口に掛ける `InteractiveUser` ポリシー。
//
// 🔴 **これはロールの軸ではない。主体の種別の軸である。** 計画 `ADR-0100` 決定 1 は利用者検索の
// 到達範囲を「全利用者」と定めた（ロールで絞らない）が、フォローアップ 2 が
// **realm のサービスアカウントも認証済みなので到達できる**ことを実装側へ戻した。
//
// 🔴 **陽性と陰性を対で置く。** 「機械は通らない」だけでは*常に失敗する*ポリシー、
// 「人は通る」だけでは*常に成功する*ポリシーと区別できない。
public class InteractiveUserPolicyTests
{
    private static IAuthorizationService Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformAuth(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal Subject(string? username, params string[] roles)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).ToList();
        if (username is not null) claims.Add(new Claim(ClaimTypes.Name, username));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal MachineClient(string clientId) =>
        // 腕 B: 利用者名が**無く** `azp` がある（`profile` を持たない機械クライアント）。
        new(new ClaimsIdentity([new Claim("azp", clientId)], "test"));

    // 🔴 陽性対照: ロールを 1 つも持たない人でも通る（共有は一般利用者の操作である）。
    [Fact]
    public async Task A_human_without_any_role_satisfies_the_policy()
        => (await Build().AuthorizeAsync(
                Subject("tanaka.taro"), null, PlatformAuthPolicies.InteractiveUser))
            .Succeeded.Should().BeTrue("ADR-0100 決定 1: 到達範囲は全利用者であり、ロールで絞らない");

    // 陰性: Keycloak のサービスアカウント（`preferred_username = service-account-<clientId>`）。
    [Fact]
    public async Task A_service_account_does_not_satisfy_the_policy()
        => (await Build().AuthorizeAsync(
                Subject("service-account-platform", PlatformAuthPolicies.ServiceRole),
                null, PlatformAuthPolicies.InteractiveUser))
            .Succeeded.Should().BeFalse("名簿の列挙を s2s の面へ出さない（IADR-0401 決定 2）");

    // 🔴 陰性（本 issue の核心）: **ロールを持つサービスアカウントも通らない。**
    // `abac-seeder` のように `platform-admin` を持つサービスアカウントが realm に居る ——
    // 「管理者なら良い」ではなく「人でなければ駄目」である。
    [Theory]
    [InlineData(PlatformAuthPolicies.AdminRole)]
    [InlineData(PlatformAuthPolicies.OperatorRole)]
    public async Task A_service_account_holding_a_user_role_still_does_not_satisfy_the_policy(string role)
        => (await Build().AuthorizeAsync(
                Subject("service-account-seeder", role), null, PlatformAuthPolicies.InteractiveUser))
            .Succeeded.Should().BeFalse("絞る軸はロールではなく主体の種別である");

    // 陰性: 利用者名を持たない機械クライアント（`MachinePrincipal` の腕 B）。
    [Fact]
    public async Task A_machine_client_without_a_username_does_not_satisfy_the_policy()
        => (await Build().AuthorizeAsync(
                MachineClient("bff"), null, PlatformAuthPolicies.InteractiveUser))
            .Succeeded.Should().BeFalse();

    // 陰性: 未認証は通らない（`RequireAuthenticatedUser`）。
    // 🔴 `MachinePrincipal.IsMachine` は未認証を false（＝機械ではない）と読むので、
    // **`RequireAuthenticatedUser()` を落とすと匿名が通る**。この試験がそれを止める。
    [Fact]
    public async Task An_anonymous_principal_does_not_satisfy_the_policy()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        MachinePrincipal.IsMachine(anonymous).Should().BeFalse(
            "陽性対照: 判定器は未認証を『機械ではない』と読む（だから認証の要求が別に要る）");
        (await Build().AuthorizeAsync(anonymous, null, PlatformAuthPolicies.InteractiveUser))
            .Succeeded.Should().BeFalse();
    }

    // 🔴 **`ServiceCaller` の裏返しではない**（別軸であることの固定）。
    [Fact]
    public async Task The_policy_is_not_the_inverse_of_service_caller()
    {
        var authz = Build();
        var human = Subject("tanaka.taro");

        (await authz.AuthorizeAsync(human, null, PlatformAuthPolicies.ServiceCaller))
            .Succeeded.Should().BeFalse("人は s2s の面を通らない");
        (await authz.AuthorizeAsync(human, null, PlatformAuthPolicies.InteractiveUser))
            .Succeeded.Should().BeTrue("人は名簿の読み口を通る");
    }

    // 名前は契約（`x-roles: []` と対になる宣言）である。**変えると経路表の固定が空振りする。**
    [Fact]
    public void The_policy_name_is_stable()
        => PlatformAuthPolicies.InteractiveUser.Should().Be("InteractiveUser");
}
