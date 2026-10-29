using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

// ADR-0004, FR-09: Keycloak のレルムロール（realm_access.roles）を ClaimTypes.Role へ展開する。
// 標準の JwtBearerHandler は realm_access をネストした JSON クレームとして保持するだけで、
// 個々のロールを role クレームへ自動展開しない。そのため RequireRole("platform-admin") が
// 実 Keycloak トークンにマッチせず、正規の管理者でも AdminOnly を通過できない。
// 本 IClaimsTransformation でトークン検証後に展開し、管理系エンドポイントの RBAC を機能させる。
public sealed class KeycloakRolesClaimsTransformation(ILogger<KeycloakRolesClaimsTransformation>? logger = null) : IClaimsTransformation
{
    // Keycloak がレルムロールを格納する JSON クレーム名（{"roles":[...]}）。
    private const string RealmAccessClaim = "realm_access";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // 未認証・非 ClaimsIdentity は素通し（fail-closed: ロールを付与しない）。
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        var realmAccess = principal.FindFirst(RealmAccessClaim)?.Value;
        if (string.IsNullOrWhiteSpace(realmAccess))
            return Task.FromResult(principal);

        // 複数回呼ばれ得る（AuthenticateAsync 毎）ため、重複を避けつつ冪等に展開する。
        foreach (var role in ExtractRoles(realmAccess, logger))
        {
            if (!identity.HasClaim(ClaimTypes.Role, role))
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        return Task.FromResult(principal);
    }

    // realm_access クレーム（例: {"roles":["platform-admin","user"]}）から roles を取り出す。
    // 不正な JSON・想定外の形状は無視する（fail-closed: ロールを付与しない）。
    // NFR-19, #2141: 黙って捨てない —— 警告を 1 行残す（値は載せない。外由来の文字列で、トークンの中身でもある）。
    // `roles` を持たないオブジェクト（ロールが 0 個）は不正ではないので記録しない。
    private static List<string> ExtractRoles(string realmAccessJson, ILogger? logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(realmAccessJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                logger?.LogWarning("realm_access クレームがオブジェクトでないためロールを付与しない（形: {Kind}）。", doc.RootElement.ValueKind);
                return [];
            }
            if (!doc.RootElement.TryGetProperty("roles", out var roles))
                return [];
            if (roles.ValueKind != JsonValueKind.Array)
            {
                logger?.LogWarning("realm_access クレームの roles が配列でないためロールを付与しない（形: {Kind}）。", roles.ValueKind);
                return [];
            }

            return roles.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.String)
                .Select(r => r.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch (JsonException)
        {
            logger?.LogWarning("realm_access クレームを JSON として読めないためロールを付与しない。");
            return [];
        }
    }
}
