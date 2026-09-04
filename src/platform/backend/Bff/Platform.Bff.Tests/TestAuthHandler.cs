using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Bff.Tests;

// FR-10: BFF テスト用認証ハンドラ。JWT/Keycloak に依存せず ClaimsPrincipal を注入する。
// 既定では管理者ロール（platform-admin）を付与し、ヘッダ "X-Test-Roles" で上書きできる。
//   - ヘッダ無し             → platform-admin（/bff/dashboard/summary の管理系ロール要求が通る）
//   - "X-Test-Roles: viewer" → 管理系以外のロール（403 になる確認用）
//   - "X-Test-Anonymous"     → 認証しない（無認証＝真の匿名リクエスト。FR-15 の存在秘匿 404 検証用）
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";

    // FR-15: JWT を一切持たない匿名リクエストを再現する（このハンドラは既定で常に認証成功するため、
    // 無認証の存在秘匿（404）を検証するには明示的に認証をスキップする必要がある）。
    public const string AnonymousHeader = "X-Test-Anonymous";

    // NFR-02, ADR-0076 決定 4, [[IADR-0378]] (#1203): 主体のクライアント識別子（`azp`）を差し替える。
    // 合成監視の標識は**検証済み JWT の主体**で判定するため、これが無いと
    // 「合成である／ない」を作り分けられず、陽性・陰性・偽装の 3 本を書けない。
    public const string ClientIdHeader = "X-Test-Client-Id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // FR-15: 無認証ケースは認証結果なし（NoResult）とし、http.User を未認証のまま通す。
        if (Request.Headers.ContainsKey(AnonymousHeader))
            return Task.FromResult(AuthenticateResult.NoResult());

        var roles = Request.Headers.TryGetValue(RolesHeader, out var header)
            ? header.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["platform-admin"];

        var claims = new List<Claim> { new(ClaimTypes.Name, "test-user") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        // #1203: Keycloak のトークンは呼び出し元クライアントを `azp` で名乗る。
        if (Request.Headers.TryGetValue(ClientIdHeader, out var clientId)
            && !string.IsNullOrWhiteSpace(clientId.ToString()))
        {
            claims.Add(new Claim("azp", clientId.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
