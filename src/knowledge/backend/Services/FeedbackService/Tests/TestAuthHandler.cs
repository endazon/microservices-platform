using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FeedbackService.Tests;

// FR-08: テスト用認証ハンドラ。JWT/Keycloak に依存せず ClaimsPrincipal を注入する。
// 既定では管理者ロール（platform-admin）を付与し、ヘッダ "X-Test-Roles" で上書きできる。
//   - ヘッダ無し          → platform-admin（一覧 AdminOnly が通る）
//   - "X-Test-Roles: viewer" → 非管理ロール（一覧 AdminOnly が 403 になる確認用）
//   - "X-Test-Anonymous: 1"   → **認証しない**（無認証が 401 になる確認用。#521 で追加）
// ※ AuthorizationService.Api.Tests.TestAuthHandler と同一方針。
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";

    // FR-08, #521: **無認証を再現するヘッダ。** 本ハンドラは従前どこからでも Success を返しており、
    // 「無認証なら 401」を試験する手段が無かった（計画 FR-08 の受け入れ基準がそれを要求している）。
    // Platform.Bff.Tests の TestAuthHandler が持つ AnonymousHeader と同じ作法である。
    public const string AnonymousHeader = "X-Test-Anonymous";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 認証しない（NoResult）。端点に RequireAuthorization があれば 401 になる。
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
