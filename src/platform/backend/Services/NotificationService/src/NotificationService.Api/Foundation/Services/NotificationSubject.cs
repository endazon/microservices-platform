using System.Security.Claims;

namespace NotificationService.Api.Foundation.Services;

// FR-22, ADR-0004, IADR-0215: 通知の宛先（主体）の解決。
//
// ★ **主体はサーバ側でトークンからだけ決める。** 要求のクエリ・本文・ヘッダから主体を
// 受け取る口を**作らない** —— 受け取れる形にした時点で、他人の通知を読む経路ができる。
// BffScopeResolver が「クライアントが指定した Scope は信頼しない」としているのと同じ判断である。
//
// 探す順は `sub`（OIDC の主体識別子。通信仕様書が名指ししている）→ `ClaimTypes.NameIdentifier`
// （JwtBearer の既定マッピングで `sub` が写る先）→ `Identity.Name`（preferred_username。
// AddPlatformAuth が NameClaimType に設定している）。
public static class NotificationSubject
{
    public const string SubClaim = "sub";

    // 解決できなければ null。**呼び出し側は 401 へ倒す**（匿名を「誰か」として扱わない）。
    public static string? Of(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return null;

        var sub = principal.FindFirst(SubClaim)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.Identity.Name;

        return string.IsNullOrWhiteSpace(sub) ? null : sub;
    }
}
