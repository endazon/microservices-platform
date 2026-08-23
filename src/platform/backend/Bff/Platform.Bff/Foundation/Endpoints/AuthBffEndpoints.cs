using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Bff.Foundation.Session;
using System.Security.Claims;

namespace Platform.Bff.Foundation.Endpoints;

// NFR, SC-16, ADR-0032, IADR-0251, #439: BFF セッションの入口。
//
// **画面そのもの（SC-13〜15 のログイン・MFA・パスワード再設定）は Keycloak テーマ側であり
// 本実装の対象ではない**（ADR-0026）。ここが担うのはセッションの土台と、SC-16（アカウント設定）の
// セッション管理との整合である。
//
// **SPA はトークンを扱わない。** ブラウザが持つのは HttpOnly の セッション Cookie だけで、
// アクセストークン／リフレッシュトークンは BFF 側（Redis のチケット）にのみ置く。
//
// 🔴 **パスが `/bff/` 配下なのは飾りではない。** エッジは `/bff` と `/bff/` しか BFF へ通さず、
// それ以外は SPA へ委譲する。フレームワーク既定（`/signin-oidc` 等）のままでは
// **認可サーバからのコールバックが BFF に永久に届かない**（IADR-0251 決定 3・実測）。
public static class AuthBffEndpoints
{
    public static IEndpointRouteBuilder MapAuthBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/auth").WithTags("Auth BFF");

        // 🔴 **セッションスキームを明示する。** 既定スキームは JwtBearer のままである（3a は
        // 受け皿を足すだけで切り替えない）。素の `RequireAuthorization()` は既定ポリシー＝
        // JwtBearer を見にいくため、**Cookie でログインしていても 401 になる。**
        var sessionOnly = new AuthorizationPolicyBuilder(BffSessionExtensions.SessionScheme)
            .RequireAuthenticatedUser()
            .Build();

        // ログイン開始。認可サーバへ送り出すだけで、戻り先は `returnUrl`（**自サイト内に限る**）。
        g.MapGet("/login", (string? returnUrl, HttpContext http) =>
        {
            var target = SafeReturnUrl(returnUrl);
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = target },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        }).WithName("BffAuthLogin").AllowAnonymous();

        // ログアウト。ブラウザのセッションと認可サーバのセッションの両方を終わらせる。
        //
        // 🔴 **GET ＋ `sid` 一致検査である（IADR-0273 決定 6）。POST に戻さないこと。**
        // 認可サーバへの往復（end-session → logout-callback）は**トップレベルナビゲーション**でしか
        // 完結しない。フォーム POST はカスタムヘッダ（CSRF の 2 枚目の壁）を付けられず、
        // fetch の POST は 302 の先へブラウザを運べない。GET にする代わりに、CSRF（強制ログアウト）は
        // **セッションの `sid` クレームと一致するクエリ**で防ぐ —— sid は HttpOnly セッションの中に
        // しか無く、攻撃者は知り得ない（Duende BFF と同じ形）。sid は `/me` の `logoutUrl` が配る。
        g.MapGet("/logout", (string? sid, string? returnUrl, HttpContext http) =>
        {
            var sessionSid = http.User.FindFirst("sid")?.Value;
            // sid を持たないセッションは照合不能＝**拒否**（fail-closed。Keycloak は常に sid を発行する）。
            if (string.IsNullOrEmpty(sessionSid) || sid != sessionSid)
                return Results.BadRequest();

            var target = SafeReturnUrl(returnUrl);
            return Results.SignOut(
                new AuthenticationProperties { RedirectUri = target },
                [BffSessionExtensions.SessionScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        }).WithName("BffAuthLogout").RequireAuthorization(sessionOnly);

        // 現在の身元。**トークンは返さない。** SPA が「誰としてログインしているか」を知る唯一の口。
        // `logoutUrl` は上のログアウト端点の sid 検査を通る形で組み立てて配る（SC-16 整合）。
        g.MapGet("/me", (HttpContext http) =>
        {
            var user = http.User;
            if (user?.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();

            return Results.Ok(new BffIdentityDto(
                user.Identity.Name ?? string.Empty,
                user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty,
                [.. user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().Order()],
                LogoutUrl(user)));
        }).WithName("BffAuthMe").RequireAuthorization(sessionOnly).Produces<BffIdentityDto>();

        return app;
    }

    /// <summary>
    /// **戻り先は自サイト内のパスに限る。** 絶対 URL や `//evil.com` を通すと
    /// オープンリダイレクトになり、ログイン導線が攻撃者のサイトへの誘導に使われる。
    /// </summary>
    internal static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/";

    /// <summary>
    /// ログアウト URL。セッションの `sid` を含める（上の GET /logout の一致検査を通る唯一の配り口）。
    /// sid を持たないセッションには**配らない**（ログアウト端点側も拒否する。fail-closed の対）。
    /// </summary>
    internal static string? LogoutUrl(ClaimsPrincipal user) =>
        user.FindFirst("sid")?.Value is { Length: > 0 } sid
            ? "/bff/auth/logout?sid=" + Uri.EscapeDataString(sid)
            : null;
}

/// <summary>SC-16: 画面が出す「今ログインしている人」。**トークンは含めない。**</summary>
public sealed record BffIdentityDto(
    string Name, string Subject, IReadOnlyList<string> Roles, string? LogoutUrl);
