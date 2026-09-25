using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ConversionService.Tests;

// NFR-09, ADR-0109 決定 3, ADR-0084 決定 1, IADR-0462 (#1520): テスト用 IdP の代わりに、BFF が中継する
// 利用者トークンと同じ形（Keycloak の `realm_access.roles`）の JWT を発行する。
//
// 🔴 **偽の認証スキーム（TestAuthHandler）は置かない。** 本サービスの門は「中継された利用者の資格情報を
// 自ら検証する」ことそのものであり、**本物の JwtBearer パイプライン**（`AddPlatformAuth` ＋
// `KeycloakRolesClaimsTransformation`）を通さないと、署名・発行元・有効期限の検証と
// `realm_access.roles` → `ClaimTypes.Role` の展開を測れない。差し替えるのは metadata の取得先
// （ネットワーク）と検証鍵だけである。LlmGateway の `TestServiceTokens`（[[IADR-0424]]）と同じ作法。
//
// 🔴 **ソケットを開かない。** 利用する `WebApplicationFactory<Program>` は TestServer（メモリ内）であり、
// どのアドレスにも bind しない。JwtBearer の metadata も静的構成へ差し替えるので外へも出ない。
internal static class TestUserTokens
{
    public const string Issuer = "https://test-issuer/realms/platform";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("conversion-user-relay-test-signing-key-0123456789abcdef"));

    // 発行元は合っているが鍵が違う（＝偽造）トークンを作るための鍵。
    private static readonly SymmetricSecurityKey ForeignKey =
        new(Encoding.UTF8.GetBytes("conversion-foreign-signing-key-not-trusted-0123456789ab"));

    /// <summary>JwtBearer の metadata 取得（ネットワーク）を静的構成へ差し替え、検証鍵をテスト用にする。</summary>
    public static void UseStaticJwtBearer(IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration { Issuer = Issuer });
            o.TokenValidationParameters.IssuerSigningKey = SigningKey;
            o.TokenValidationParameters.ValidIssuer = Issuer;
        });

    /// <summary>realm ロールつきの利用者トークンを発行する（BFF が中継するものと同じ形）。</summary>
    public static string Issue(string subject, IEnumerable<string> realmRoles,
        string issuer = Issuer, bool forged = false, bool expired = false)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            IssuedAt = expired ? now.AddHours(-2) : now,
            NotBefore = expired ? now.AddHours(-2) : now.AddMinutes(-1),
            Expires = expired ? now.AddHours(-1) : now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(forged ? ForeignKey : SigningKey,
                SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["preferred_username"] = subject,
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = realmRoles.ToArray() },
            },
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>管理者の利用者トークンを既定の資格情報として載せる（門の向こう側を測る既存テスト用）。</summary>
    public static void AuthenticateAsAdmin(HttpClient client) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Issue("test-admin", ["platform-admin"]));
}
