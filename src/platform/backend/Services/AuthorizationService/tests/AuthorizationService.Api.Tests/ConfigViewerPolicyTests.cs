using System.Security.Claims;
using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthorizationService.Api.Tests;

// FR-15, SC-11, IADR-0030: ConfigViewer ポリシーの単体テスト。
// AddPlatformAuth が登録する認可ポリシーを IAuthorizationService で直接評価し、
// 管理者・運用者のいずれか一方の保持で許可、どちらも無ければ拒否（fail-closed）を確認する。
public class ConfigViewerPolicyTests
{
    private static IAuthorizationService BuildAuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformAuth(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal AuthenticatedUserWithRoles(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "test-user") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(
            claims, authenticationType: "TestJwt", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role));
    }

    [Theory]
    [InlineData(PlatformAuthPolicies.AdminRole)]
    [InlineData(PlatformAuthPolicies.OperatorRole)]
    [InlineData(PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole)]
    public async Task ConfigViewer_AllowsAdminOrOperatorRole(params string[] roles)
    {
        var authz = BuildAuthorizationService();
        var user = AuthenticatedUserWithRoles(roles);

        var result = await authz.AuthorizeAsync(user, resource: null, PlatformAuthPolicies.ConfigViewer);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ConfigViewer_DeniesAuthenticatedUserWithoutEitherRole()
    {
        var authz = BuildAuthorizationService();
        var user = AuthenticatedUserWithRoles("viewer");

        var result = await authz.AuthorizeAsync(user, resource: null, PlatformAuthPolicies.ConfigViewer);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ConfigViewer_DeniesAnonymousUser()
    {
        var authz = BuildAuthorizationService();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // 未認証

        var result = await authz.AuthorizeAsync(anonymous, resource: null, PlatformAuthPolicies.ConfigViewer);

        result.Succeeded.Should().BeFalse();
    }

    // FR-09 リグレッション: 運用者は管理系（AdminOnly）を通過できないこと。
    [Fact]
    public async Task AdminOnly_DeniesOperatorRole()
    {
        var authz = BuildAuthorizationService();
        var operatorUser = AuthenticatedUserWithRoles(PlatformAuthPolicies.OperatorRole);

        var result = await authz.AuthorizeAsync(operatorUser, resource: null, PlatformAuthPolicies.AdminOnly);

        result.Succeeded.Should().BeFalse();
    }
}
