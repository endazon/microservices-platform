using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgePlatform.IntegrationTests.Fixtures;

// FR-09, ADR-0004: 統合テスト用の認証ハンドラ。実 Keycloak が無い環境で管理系エンドポイント
// （AdminOnly）を検証するため、platform-admin ロールを持つ ClaimsPrincipal を注入する。
// 実 JWT → realm_access.roles → ロールクレーム展開の検証は
// KeycloakRolesClaimsTransformationTests（単体）が担う。
public sealed class IntegrationTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "integration-admin"),
            new Claim(ClaimTypes.Role, "platform-admin")
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
