using System.Security.Claims;
using FluentAssertions;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthorizationService.Api.Tests;

// FR-15, SC-11, IADR-0029: AdminOrOperator ポリシーの単体テスト。
// AddKnowledgePlatformAuth が登録する認可ポリシーを IAuthorizationService で直接評価し、
// 管理者・運用者のいずれか一方の保持で許可、どちらも無ければ拒否（fail-closed）を確認する。
public class AdminOrOperatorPolicyTests
{
    private static IAuthorizationService BuildAuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKnowledgePlatformAuth(new ConfigurationBuilder().Build());
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
    [InlineData(KnowledgePlatformAuthPolicies.AdminRole)]
    [InlineData(KnowledgePlatformAuthPolicies.OperatorRole)]
    [InlineData(KnowledgePlatformAuthPolicies.AdminRole, KnowledgePlatformAuthPolicies.OperatorRole)]
    public async Task AdminOrOperator_AllowsAdminOrOperatorRole(params string[] roles)
    {
        var authz = BuildAuthorizationService();
        var user = AuthenticatedUserWithRoles(roles);

        var result = await authz.AuthorizeAsync(user, resource: null, KnowledgePlatformAuthPolicies.AdminOrOperator);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task AdminOrOperator_DeniesAuthenticatedUserWithoutEitherRole()
    {
        var authz = BuildAuthorizationService();
        var user = AuthenticatedUserWithRoles("viewer");

        var result = await authz.AuthorizeAsync(user, resource: null, KnowledgePlatformAuthPolicies.AdminOrOperator);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task AdminOrOperator_DeniesAnonymousUser()
    {
        var authz = BuildAuthorizationService();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // 未認証

        var result = await authz.AuthorizeAsync(anonymous, resource: null, KnowledgePlatformAuthPolicies.AdminOrOperator);

        result.Succeeded.Should().BeFalse();
    }

    // FR-09 リグレッション: 運用者は管理系（AdminOnly）を通過できないこと。
    [Fact]
    public async Task AdminOnly_DeniesOperatorRole()
    {
        var authz = BuildAuthorizationService();
        var operatorUser = AuthenticatedUserWithRoles(KnowledgePlatformAuthPolicies.OperatorRole);

        var result = await authz.AuthorizeAsync(operatorUser, resource: null, KnowledgePlatformAuthPolicies.AdminOnly);

        result.Succeeded.Should().BeFalse();
    }
}
