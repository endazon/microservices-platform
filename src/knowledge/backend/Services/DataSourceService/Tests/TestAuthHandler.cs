using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataSourceService.Tests;

// FR-09, IADR-0044: テスト用認証ハンドラ。JWT/Keycloak に依存せず ClaimsPrincipal を注入する。
// 既定では管理者ロール（platform-admin）を付与し、ヘッダ "X-Test-Roles" で上書きできる。
//   - ヘッダ無し                 → platform-admin（/datasources の admin/operator 要求が通る）
//   - "X-Test-Roles: platform-operator" → 運用者（同上）
//   - "X-Test-Roles: viewer"     → 非権限ロール（403 になる確認用）
// ※ DashboardService.Tests.TestAuthHandler と同一方針。
// FR-05, SC-06, IADR-0468 (#754): "X-Test-GroupPaths: /department/sales,/clearance/internal" で
//   登録者の所属グループのフルパス（クレーム group_paths。Keycloak の配列クレームと同じく 1 値 1 クレーム）を載せる。
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";
    public const string GroupPathsHeader = "X-Test-GroupPaths";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var roles = Request.Headers.TryGetValue(RolesHeader, out var header)
            ? header.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["platform-admin"];

        var claims = new List<Claim> { new(ClaimTypes.Name, "test-user") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (Request.Headers.TryGetValue(GroupPathsHeader, out var groupPaths))
            claims.AddRange(groupPaths.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => new Claim("group_paths", p)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
