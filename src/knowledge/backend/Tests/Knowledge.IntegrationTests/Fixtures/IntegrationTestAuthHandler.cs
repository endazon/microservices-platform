using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knowledge.IntegrationTests.Fixtures;

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

    /// <summary>
    /// FR-05, NFR-09, 計画 ADR-0088 決定 2, [[IADR-0413]] 決定 3, [[IADR-0414]] (#1336):
    /// ロールを要求ごとに差し替えるヘッダ（カンマ区切り）。**未指定なら従来どおり `platform-admin`。**
    ///
    /// 🔴 **既定へ `platform-service` を足す形は採らない。** 足すと、統合テストの主体が
    /// 「管理者でありサービスでもある」という**実配備に存在しない principal** になり、
    /// 面ごとに資格が違うこと（管理系は `AdminOnly`・スコープ解決は `ServiceCaller`）を
    /// **測れなくなる** —— `ADR-0088` 決定 2 が要求した統制がテストから見えなくなる。
    /// </summary>
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var roles = Request.Headers.TryGetValue(RolesHeader, out var header)
            ? header.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["platform-admin"];

        var claims = new List<Claim> { new(ClaimTypes.Name, "integration-admin") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
