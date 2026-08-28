using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentService.Tests;

// FR-09, IADR-0044: テスト用認証ハンドラ。JWT/Keycloak に依存せず ClaimsPrincipal を注入する。
// 既定では管理者ロール（platform-admin）を付与し、ヘッダ "X-Test-Roles" で上書きできる。
//   - ヘッダ無し                 → platform-admin（書き込みの admin/operator 要求が通る）
//   - "X-Test-Roles: platform-operator" → 運用者（同上）
//   - "X-Test-Roles: viewer"     → 非権限ロール（書き込みが 403 になる確認用。読み取りは可）
// ※ DashboardService.Tests.TestAuthHandler と同一方針。
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";

    // FR-21, ADR-0036 D-02: 主体（`${current_user}`）を差し替えるヘッダ。
    // 所有者ベースの動的束縛は**主体が変われば結果が変わる**ため、
    // 「別の利用者として同じ文書 ID へ書き込む」（受け入れ基準 ⑧）を試すには主体を変えられる必要がある。
    public const string UserHeader = "X-Test-User";
    public const string DefaultUser = "test-user";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var roles = Request.Headers.TryGetValue(RolesHeader, out var header)
            ? header.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["platform-admin"];

        var user = Request.Headers.TryGetValue(UserHeader, out var userHeader)
            && !string.IsNullOrWhiteSpace(userHeader.ToString())
            ? userHeader.ToString()
            : DefaultUser;

        var claims = new List<Claim> { new(ClaimTypes.Name, user) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
