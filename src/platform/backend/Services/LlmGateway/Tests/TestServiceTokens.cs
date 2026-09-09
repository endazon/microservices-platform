using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LlmGateway.Tests;

// NFR-09, ADR-0004, ADR-0084 決定 1, [[IADR-0379]] 決定 4, [[IADR-0424]] (#1364):
// テスト用 IdP の代わりに JWT を発行し、JwtBearer の検証鍵と issuer をテスト用の対称鍵へ差し替える。
//
// 🔴 **偽の認証スキーム（TestAuthHandler）は置かない。** s2s の検証は**本物の JwtBearer パイプライン**
// （`AddPlatformAuth` ＋ `KeycloakRolesClaimsTransformation`）を通してこそ意味がある ——
// `realm_access.roles` が `ClaimTypes.Role` へ展開されるところまで含めて `ServiceCaller` だからである。
// これは `GrpcKestrelFactory` が #1255 で採った作法であり、**REST の器も同じものを使う**
// （器ごとに認証の作り方が違うと、面によって測っているものが変わる）。
internal static class TestServiceTokens
{
    public const string Issuer = "https://test-issuer/realms/platform";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("grpc-s2s-test-signing-key-0123456789abcdef-0123456789"));

    /// <summary>JwtBearer の metadata 取得（ネットワーク）を静的構成へ差し替える。</summary>
    public static void UseStaticJwtBearer(IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration { Issuer = Issuer });
            o.TokenValidationParameters.IssuerSigningKey = SigningKey;
            o.TokenValidationParameters.ValidIssuer = Issuer;
        });

    /// <summary>
    /// realm ロールつきの JWT を発行する。`realm_access.roles` は
    /// `KeycloakRolesClaimsTransformation` が `ClaimTypes.Role` へ展開する（実 Keycloak と同じ形）。
    /// </summary>
    public static string IssueToken(string subject, IEnumerable<string> realmRoles)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["preferred_username"] = subject,
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = realmRoles.ToArray() },
            },
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
