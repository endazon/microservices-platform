using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationService.Api.Foundation.Services;

namespace NotificationService.Api.Tests;

// FR-22: テスト用認証ハンドラ。Keycloak / JWT に依存せず ClaimsPrincipal を注入する。
//
// ★ **主体（sub）をヘッダで指定できるようにしてあるのはテストの器だけの都合である。**
// 本体のエンドポイントは主体をトークンからしか採らない —— 器がヘッダで差し替えられるのは、
// 「別人としてアクセスする」を再現するためであって、本体に主体の口があるからではない。
//   - ヘッダ無し            → 未認証（401 の確認用）
//   - "X-Test-Sub: alice"   → alice として認証
//   - "X-Test-Roles: a,b"   → 指定ロール（通知はロールを問わないので既定は空である）
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string SubHeader = "X-Test-Sub";
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SubHeader, out var sub) || string.IsNullOrWhiteSpace(sub.ToString()))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new(NotificationSubject.SubClaim, sub.ToString()),
            new(ClaimTypes.Name, sub.ToString()),
        };

        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            claims.AddRange(roles.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => new Claim(ClaimTypes.Role, r)));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
