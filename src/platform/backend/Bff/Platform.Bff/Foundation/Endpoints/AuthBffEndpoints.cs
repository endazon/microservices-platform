using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Bff.Foundation.Session;
using System.Security.Claims;

namespace Platform.Bff.Foundation.Endpoints;

// NFR, SC-16, ADR-0032, IADR-0249, #439: BFF セッションの入口。
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
// **認可サーバからのコールバックが BFF に永久に届かない**（IADR-0249 決定 3・実測）。
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
        g.MapPost("/logout", (string? returnUrl) =>
        {
            var target = SafeReturnUrl(returnUrl);
            return Results.SignOut(
                new AuthenticationProperties { RedirectUri = target },
                [BffSessionExtensions.SessionScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        }).WithName("BffAuthLogout").RequireAuthorization(sessionOnly);

        // 現在の身元。**トークンは返さない。** SPA が「誰としてログインしているか」を知る唯一の口。
        g.MapGet("/me", (HttpContext http) =>
        {
            var user = http.User;
            if (user?.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();

            return Results.Ok(new BffIdentityDto(
                user.Identity.Name ?? string.Empty,
                user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty,
                [.. user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().Order()]));
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
}

/// <summary>SC-16: 画面が出す「今ログインしている人」。**トークンは含めない。**</summary>
public sealed record BffIdentityDto(string Name, string Subject, IReadOnlyList<string> Roles);
