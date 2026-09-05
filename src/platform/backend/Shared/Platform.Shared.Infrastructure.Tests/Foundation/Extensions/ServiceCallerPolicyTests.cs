using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// NFR-09, ADR-0004, ADR-0029, IADR-0379 決定 4 (#1201): east-west gRPC の面に掛ける ServiceCaller ポリシーを固定する。
//
// 🔴 利用者のロール（platform-admin / platform-operator）では通らないこと（陰性対照）が要点である。
// 利用者のトークンが通ると「利用者が直接呼んだ」とサービスの呼び出しを区別できなくなる（confused deputy）。
public class ServiceCallerPolicyTests
{
    private static IAuthorizationService Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformAuth(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal WithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "test"));

    [Fact]
    public async Task Service_role_satisfies_service_caller()
    {
        var result = await Build().AuthorizeAsync(WithRoles(PlatformAuthPolicies.ServiceRole), null, PlatformAuthPolicies.ServiceCaller);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(PlatformAuthPolicies.AdminRole)]
    [InlineData(PlatformAuthPolicies.OperatorRole)]
    public async Task User_roles_do_not_satisfy_service_caller(string role)
    {
        var result = await Build().AuthorizeAsync(WithRoles(role), null, PlatformAuthPolicies.ServiceCaller);

        result.Succeeded.Should().BeFalse("利用者のロールはサービス間の面を通れない");
    }

    [Fact]
    public async Task Service_role_does_not_satisfy_admin_only()
    {
        var result = await Build().AuthorizeAsync(WithRoles(PlatformAuthPolicies.ServiceRole), null, PlatformAuthPolicies.AdminOnly);

        result.Succeeded.Should().BeFalse("s2s の資格情報で利用者向けの管理面を開けない（別軸）");
    }

    [Fact]
    public void Role_and_policy_names_are_stable()
    {
        PlatformAuthPolicies.ServiceRole.Should().Be("platform-service");
        PlatformAuthPolicies.ServiceCaller.Should().Be("ServiceCaller");
    }
}
