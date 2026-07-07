using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;

// ADR-0004, FR-09: 認可ポリシー／ロールの名称定数（各サービスから参照）
public static class KnowledgePlatformAuthPolicies
{
    // FR-09: 属性辞書・ABAC ポリシーの管理は管理者のみ許可する。
    public const string AdminOnly = "AdminOnly";

    // FR-15, SC-11, IADR-0029: 構成情報の閲覧は管理者または運用者に許可する。
    public const string AdminOrOperator = "AdminOrOperator";

    // 管理者ロール（Keycloak のレルムロール想定）。
    public const string AdminRole = "platform-admin";

    // FR-15, IADR-0029: 運用者ロール（構成閲覧のみ。管理系操作は不可）。
    public const string OperatorRole = "platform-operator";
}

public static class AuthExtensions
{
    // ADR-0004: Keycloak OIDC/JWT 認証（P0: 認証のみ、P2: ABAC 認可を追加）
    public static IServiceCollection AddKnowledgePlatformAuth(
        this IServiceCollection services,
        IConfiguration config)
    {
        var authority = config["Auth:Authority"]
            ?? "http://keycloak:8080/realms/knowledge-platform";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters.ValidateAudience = false;
                // FR-09: RequireRole/IsInRole が参照するロールクレーム型を明示する。
                // 実 Keycloak のレルムロールは realm_access.roles に格納され、標準ハンドラでは
                // ClaimTypes.Role へ展開されないため、下記の IClaimsTransformation で補う。
                options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
            });

        // FR-09, ADR-0004: Keycloak の realm_access.roles を ClaimTypes.Role へ展開する。
        // これがないと RequireRole("platform-admin") が実トークンにマッチしない。
        services.AddTransient<IClaimsTransformation, KeycloakRolesClaimsTransformation>();

        // FR-09: 管理系エンドポイント用に管理者ロールポリシーを登録する。
        services.AddAuthorization(options =>
        {
            options.AddPolicy(KnowledgePlatformAuthPolicies.AdminOnly, policy =>
                policy.RequireRole(KnowledgePlatformAuthPolicies.AdminRole));

            // FR-15, SC-11, IADR-0029: 構成情報 API・構成ビューア用。
            // RequireRole の複数指定はいずれか一致（OR）で許可する。
            options.AddPolicy(KnowledgePlatformAuthPolicies.AdminOrOperator, policy =>
                policy.RequireRole(
                    KnowledgePlatformAuthPolicies.AdminRole,
                    KnowledgePlatformAuthPolicies.OperatorRole));
        });
        return services;
    }
}
