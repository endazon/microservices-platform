using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphService.Tests;

// FR-17: テスト用認証ハンドラ。JWT/Keycloak に依存せず ClaimsPrincipal を注入する。
//   - ヘッダ無し            → 認証済み（test-user）
//   - "X-Test-Anonymous: 1" → **認証しない**（RequireAuthorization が 401 を返すことの確認用）
// ※ FeedbackService.Tests.TestAuthHandler と同一方針。
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string AnonymousHeader = "X-Test-Anonymous";

    // FR-17, SC-09: 辺の型辞書は書きが管理者・読みが運用者/管理者に限られる（#910）。
    // 既定は platform-admin を与え、ヘッダで落として 403 を試験できるようにする
    // （FeedbackService.Tests.TestAuthHandler と同一方針）。
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey(AnonymousHeader))
            return Task.FromResult(AuthenticateResult.NoResult());

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
