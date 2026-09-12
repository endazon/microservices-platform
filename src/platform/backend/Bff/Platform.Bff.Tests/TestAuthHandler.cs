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

    // FR-05, ADR-0080, [[IADR-0411]], [[IADR-0417]] (#1255): ABAC 利用者属性のクレームを足す。
    // 書式は `clearance=secret;department=sales`（`;` 区切り。多値は同じキーを 2 回書く）。
    //
    // 🔴 **これが無いと、属性を 1 つも運ばない実装が緑のままになる。**
    // 既定の主体は `clearance` / `department` / 集合値キーのクレームを 1 つも持たないため、
    // `BffScopeResolver.ExtractUserAttributes` は**常に空**を返す ——
    // 「利用者文脈を運ぶ」ことを測る試験が、**空を運んでも成立してしまう**（実測で確認した）。
    public const string AttributesHeader = "X-Test-Attributes";

    // FR-19, 計画 ADR-0100 フォローアップ 2, [[IADR-0449]] (#1447): **主体の利用者名を差し替える。**
    // 🔴 これが無いと「人か機械か」を作り分けられない —— `MachinePrincipal.IsMachine` は
    // 利用者名が `service-account-` で始まるかで判定するため、既定の `test-user` では
    // **サービスアカウントが 403 になることを測れない**（`InteractiveUser` ポリシーの陰性対照）。
    public const string UsernameHeader = "X-Test-Username";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // FR-15: 無認証ケースは認証結果なし（NoResult）とし、http.User を未認証のまま通す。
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
        // #1203: Keycloak のトークンは呼び出し元クライアントを `azp` で名乗る。
        if (Request.Headers.TryGetValue(ClientIdHeader, out var clientId)
            && !string.IsNullOrWhiteSpace(clientId.ToString()))
        {
            claims.Add(new Claim("azp", clientId.ToString()));
        }

        // #1255: ABAC 利用者属性（`clearance=secret;department=sales`）。同じキーを 2 回書けば多値になる。
        if (Request.Headers.TryGetValue(AttributesHeader, out var attributes))
        {
            foreach (var pair in attributes.ToString()
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Length > 0)
                    claims.Add(new Claim(parts[0], parts[1]));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
