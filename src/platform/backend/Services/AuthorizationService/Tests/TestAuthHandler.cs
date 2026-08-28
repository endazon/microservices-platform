using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthorizationService.Tests;

// FR-09: テスト用認証ハンドラ。JWT/Keycloak に依存せず ClaimsPrincipal を注入する。
// 既定では管理者ロール（platform-admin）を付与し、ヘッダ "X-Test-Roles" で上書きできる。
//   - ヘッダ無し          → platform-admin（管理系テストが通る）
//   - "X-Test-Roles: a,b" → 指定ロール（例: "viewer" のみ → AdminOnly が 403 になる確認用）
// ※ 空値ヘッダは HTTP で送信されずヘッダ無し扱い（既定 admin）になるため、
//   非管理ケースは管理者以外のロールを明示的に指定すること。
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var roles = Request.Headers.TryGetValue(RolesHeader, out var header)
            ? header.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["platform-admin"];

        var claims = new List<Claim> { new(ClaimTypes.Name, "test-user") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
