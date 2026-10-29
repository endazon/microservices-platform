using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Tests;

// FR-09, ADR-0004: Keycloak の realm_access.roles → ClaimTypes.Role 展開の単体テスト。
// 実 JwtBearer が realm_access をネストした JSON クレームのまま保持する挙動を模し、
// 展開後に IsInRole("platform-admin") が成立すること（= AdminOnly が正規管理者を許可）を確認する。
[Trait("TestKind", "Unit")]
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

    // ── NFR-19, #2141: 不正な realm_access は黙って捨てない（警告を 1 行。値は載せない）──────────
    //
    // 従前はロールを付けない（fail-closed）だけで記録が無く、管理者が全員 403 になっても理由を追えなかった。
    // 値（トークンの中身）はログに載せない —— 外由来の文字列であり、行の偽装（NFR-28）と情報の露出の両方を避ける。

    private sealed class CapturingLogger : ILogger<KeycloakRolesClaimsTransformation>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    [Theory]
    [InlineData("not-json-SECRETVALUE")]
    [InlineData("""{"roles":"SECRETVALUE"}""")]
    [InlineData("""["SECRETVALUE"]""")]
    public async Task Transform_MalformedRealmAccess_LogsOneWarningWithoutTheValue(string realmAccess)
    {
        var log = new CapturingLogger();
        var sut = new KeycloakRolesClaimsTransformation(log);

        var result = await sut.TransformAsync(PrincipalWith(
            new Claim(ClaimTypes.Name, "bad-token"), new Claim("realm_access", realmAccess)));

        result.FindAll(ClaimTypes.Role).Should().BeEmpty("fail-closed は変えない");
        log.Lines.Should().ContainSingle(l => l.Level == LogLevel.Warning);
        log.Lines.Single().Message.Should().Contain("realm_access").And.NotContain("SECRETVALUE");
    }

    [Fact]
    public async Task Transform_WellFormedOrAbsentRealmAccess_LogsNothing()
    {
        var log = new CapturingLogger();
        var sut = new KeycloakRolesClaimsTransformation(log);

        await sut.TransformAsync(PrincipalWith(new Claim(ClaimTypes.Name, "a"),
            new Claim("realm_access", """{"roles":["platform-admin"]}""")));
        await sut.TransformAsync(PrincipalWith(new Claim(ClaimTypes.Name, "b")));
        // roles を持たない形（ロールが 0 個の利用者）は不正ではない。
        await sut.TransformAsync(PrincipalWith(new Claim(ClaimTypes.Name, "c"), new Claim("realm_access", "{}")));

        log.Lines.Should().BeEmpty();
    }
}
