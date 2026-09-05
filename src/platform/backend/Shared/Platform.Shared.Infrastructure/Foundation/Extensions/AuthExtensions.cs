using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

// ADR-0004, FR-09: 認可ポリシー／ロールの名称定数（各サービスから参照）
public static class PlatformAuthPolicies
{
    // FR-09: 属性辞書・ABAC ポリシーの管理は管理者のみ許可する。
    public const string AdminOnly = "AdminOnly";

    // FR-15, SC-11, IADR-0030: 構成情報の閲覧は管理者・運用者ロールに限定する。
    public const string ConfigViewer = "ConfigViewer";

    // 管理者ロール（Keycloak のレルムロール想定）。
    public const string AdminRole = "platform-admin";

    // FR-15, SC-11, IADR-0030: 運用者ロール（Keycloak のレルムロール想定。構成閲覧のみ。管理系操作は不可）。
    public const string OperatorRole = "platform-operator";

    // NFR-09, ADR-0029, ADR-0075, IADR-0379 決定 4 (#1201): east-west gRPC の呼び出し側サービスに要求する
    // ポリシー。**利用者のロール（AdminOnly / ConfigViewer）とは別軸**であり、利用者のトークンでは通らない
    // （通ると「利用者が直接呼んだ」と区別できず、confused deputy になる）。
    public const string ServiceCaller = "ServiceCaller";

    // サービスアカウント（client credentials で得た JWT）に付けるレルムロール。realm の各 confidential client の
    // service account へ付与する（deploy/keycloak/microservices-platform-realm.json）。
    public const string ServiceRole = "platform-service";
}

public static class AuthExtensions
{
    // ADR-0004: Keycloak OIDC/JWT 認証（P0: 認証のみ、P2: ABAC 認可を追加）
    public static IServiceCollection AddPlatformAuth(
        this IServiceCollection services,
        IConfiguration config)
    {
        var authority = config["Auth:Authority"]
            ?? "http://keycloak:8080/realms/platform";

        // NFR(運用性/セキュリティ), ADR-0004, IADR-0076 手順B, IADR-0086: OIDC metadata の取得先と issuer 検証値を
        // 分離できるようにする（単一エッジ host OIDC を CoreDNS/hosts 改変なしに成立させる）。既定（両キー未設定）は
        // Authority 一本の現行挙動に縮退＝後方互換・fail-safe。issuer 検証は弱めない（下記参照）。
        var metadataAddress = config["Auth:MetadataAddress"];
        var validIssuers = ParseValidIssuers(config["Auth:ValidIssuers"]);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // IADR-0086 決定1: MetadataAddress 設定時は in-cluster から到達できる well-known を metadata 取得先に
                // 用い、Authority とは排他にする（両設定時の暗黙優先順位に依存しない）。未設定時は現行どおり Authority。
                if (!string.IsNullOrWhiteSpace(metadataAddress))
                {
                    options.MetadataAddress = metadataAddress;
                }
                else
                {
                    options.Authority = authority;
                }
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters.ValidateAudience = false;
                // IADR-0086 決定3: issuer 検証は弱めない（ValidateIssuer=true のまま）。ValidIssuers は「token の iss として
                // 追加で受理する発行元 URL の許可リスト」で、エッジ host issuer（手順B）を足す。JwtBearer ハンドラは
                // metadata 由来の issuer を常に受理集合へ併合するため、in-cluster issuer（手順A）と併存＝後方互換。
                // 署名鍵(JWKS)は信頼できる in-cluster metadata から取得するため、許可リスト追加で攻撃面は広がらない。
                if (validIssuers.Length > 0)
                {
                    options.TokenValidationParameters.ValidIssuers = validIssuers;
                }
                // FR-09: RequireRole/IsInRole が参照するロールクレーム型を明示する。
                // 実 Keycloak のレルムロールは realm_access.roles に格納され、標準ハンドラでは
                // ClaimTypes.Role へ展開されないため、下記の IClaimsTransformation で補う。
                options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                // FR-08, FR-15, IADR-0010, IADR-0031: Identity.Name が参照する名前クレームを Keycloak の
                // preferred_username に合わせる。既定マップは unique_name のみ写像するため、実
                // Keycloak トークンでは Name が null になり、送信者特定（FR-08 の userId・構成 API
                // 監査ログの subject）が全員 anonymous/unknown へ潰れる（Issue #118 監査で実測）。
                options.TokenValidationParameters.NameClaimType = "preferred_username";
            });

        // FR-09, ADR-0004: Keycloak の realm_access.roles を ClaimTypes.Role へ展開する。
        // これがないと RequireRole("platform-admin") が実トークンにマッチしない。
        services.AddTransient<IClaimsTransformation, KeycloakRolesClaimsTransformation>();

        // FR-09: 管理系エンドポイント用に管理者ロールポリシーを登録する。
        services.AddAuthorization(options =>
        {
            options.AddPolicy(PlatformAuthPolicies.AdminOnly, policy =>
                policy.RequireRole(PlatformAuthPolicies.AdminRole));

            // FR-15, SC-11, IADR-0030: 構成情報 API・構成ビューア用。
            // RequireRole の複数指定はいずれか一致（OR）で許可する。
            options.AddPolicy(PlatformAuthPolicies.ConfigViewer, policy =>
                policy.RequireRole(
                    PlatformAuthPolicies.AdminRole,
                    PlatformAuthPolicies.OperatorRole));

            // NFR-09, IADR-0379 決定 4 (#1201): east-west gRPC の面に掛ける。呼び出し側サービス自身の
            // 資格情報（`platform-service`）だけを通し、利用者のトークンは（管理者であっても）通さない。
            options.AddPolicy(PlatformAuthPolicies.ServiceCaller, policy =>
                policy.RequireRole(PlatformAuthPolicies.ServiceRole));
        });
        return services;
    }

    // IADR-0086 決定1: Auth:ValidIssuers を単一 env 文字列（chart から注入）としてカンマ/空白区切りでパースする。
    // 空要素は落とし、各要素を trim する。未設定/空なら空配列（＝ValidIssuers を設定しない＝現行挙動）。
    private static string[] ParseValidIssuers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        return raw
            .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }
}
