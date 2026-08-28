using System.Security.Claims;
using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Tests;

// FR-09, ADR-0004: Keycloak の realm_access.roles → ClaimTypes.Role 展開の単体テスト。
// 実 JwtBearer が realm_access をネストした JSON クレームのまま保持する挙動を模し、
// 展開後に IsInRole("platform-admin") が成立すること（= AdminOnly が正規管理者を許可）を確認する。
public class KeycloakRolesClaimsTransformationTests
{
    private static ClaimsPrincipal PrincipalWith(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "TestJwt", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role));

    [Fact]
    public async Task Transform_ExpandsRealmAccessRoles_IntoRoleClaims()
    {
        var sut = new KeycloakRolesClaimsTransformation();
        var principal = PrincipalWith(
            new Claim(ClaimTypes.Name, "admin-user"),
            new Claim("realm_access", """{"roles":["platform-admin","user"]}"""));

        var result = await sut.TransformAsync(principal);

        result.IsInRole("platform-admin").Should().BeTrue();
        result.IsInRole("user").Should().BeTrue();
        result.IsInRole("nonexistent").Should().BeFalse();
    }

    [Fact]
    public async Task Transform_NoRealmAccess_AddsNoRoles()
    {
        var sut = new KeycloakRolesClaimsTransformation();
        var principal = PrincipalWith(new Claim(ClaimTypes.Name, "no-roles-user"));

        var result = await sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Should().BeEmpty();
        result.IsInRole("platform-admin").Should().BeFalse();
    }

    [Fact]
    public async Task Transform_MalformedRealmAccess_AddsNoRoles_FailClosed()
    {
        var sut = new KeycloakRolesClaimsTransformation();
        var principal = PrincipalWith(
            new Claim(ClaimTypes.Name, "bad-token"),
            new Claim("realm_access", "not-json"));

        var result = await sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_IsIdempotent_NoDuplicateRoleClaims()
    {
        var sut = new KeycloakRolesClaimsTransformation();
        var principal = PrincipalWith(
            new Claim(ClaimTypes.Name, "admin-user"),
            new Claim("realm_access", """{"roles":["platform-admin"]}"""));

        await sut.TransformAsync(principal);
        var result = await sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Count(c => c.Value == "platform-admin").Should().Be(1);
    }
}
