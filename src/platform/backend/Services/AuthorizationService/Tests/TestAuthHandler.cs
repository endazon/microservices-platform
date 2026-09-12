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

    // FR-19, 計画 ADR-0100 フォローアップ 2, [[IADR-0449]] (#1447): **主体の利用者名を差し替える。**
    // 🔴 これが無いと「人か機械か」を作り分けられない —— `MachinePrincipal.IsMachine` は
    // 利用者名が `service-account-` で始まるかで判定するため、既定の `test-user` では
    // **サービスアカウントが 403 になることを測れない**（陰性対照が書けない）。
    public const string UsernameHeader = "X-Test-Username";

    // FR-19, #1447: JWT を一切持たない匿名リクエストを再現する（このハンドラは既定で常に認証成功
    // するため、401 を測るには明示的に認証をスキップする必要がある。BFF 側の同名ヘッダと揃える）。
    public const string AnonymousHeader = "X-Test-Anonymous";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // #1447: 無認証ケースは認証結果なし（NoResult）とし、http.User を未認証のまま通す。
        if (Request.Headers.ContainsKey(AnonymousHeader))
            return Task.FromResult(AuthenticateResult.NoResult());

        var roles = Request.Headers.TryGetValue(RolesHeader, out var header)
            ? header.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["platform-admin"];

        // #1447: 既定は従前どおり `test-user`（人）。ヘッダで `service-account-<clientId>` を名乗れる。
        var username = Request.Headers.TryGetValue(UsernameHeader, out var name)
            && !string.IsNullOrWhiteSpace(name.ToString())
                ? name.ToString().Trim()
                : "test-user";

        var claims = new List<Claim> { new(ClaimTypes.Name, username) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
