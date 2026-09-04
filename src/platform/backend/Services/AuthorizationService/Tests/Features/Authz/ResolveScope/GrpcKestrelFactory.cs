using System.Text;
using AuthorizationService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AuthorizationService.Tests.Features.Authz.ResolveScope;

// FR-05, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379 (#1201): gRPC 参照実装の器。
//
// 🔴 **TestServer ではなく実 Kestrel で起こす。** TestServer は in-memory であり、h2c（TLS 無し HTTP/2）の
// ポートが実際に bind され、HTTP/1.1 のポートが**消えていない**ことを観測できない
// （AddPlatformGrpcListener の 🔴 を参照）。`WebApplicationFactory.UseKestrel()` で実ポートへ bind し、
// gRPC 用ポートは本器が空きポートを選んで `Grpc:Port` として渡す。
//
// 認証は実 IdP を持たないので、JwtBearer の検証鍵と issuer をテスト用の対称鍵へ差し替える
// （metadata 取得は StaticConfigurationManager で止める）。TestAuthHandler は使わない —— s2s の
// 検証は**本物の JwtBearer パイプライン**（AddPlatformAuth ＋ KeycloakRolesClaimsTransformation）を
// 通してこそ意味がある。
//
// h2c ポートと HTTP/1.1 側の URL は GrpcTestConfiguration が**環境変数**で与える（ConfigureAppConfiguration
// では builder 時点の読み取りに間に合わない。同ファイルの注記）。
public sealed class GrpcKestrelFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://test-issuer/realms/platform";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("grpc-s2s-test-signing-key-0123456789abcdef-0123456789"));

    private readonly string _dbName = $"AuthzGrpc_{Guid.NewGuid()}";

    // ポートは GrpcTestConfiguration（環境変数）が決める。ConfigureAppConfiguration では間に合わない。
    public int GrpcPort => GrpcTestConfiguration.GrpcPort;

    public GrpcKestrelFactory()
    {
        UseKestrel();
    }

    public string GrpcAddress => $"http://127.0.0.1:{GrpcPort}";

    // HTTP/1.1 側（REST・/health/*）の実アドレス。gRPC を有効にしてもこちらが残っていることの証明に使う。
    public string HttpAddress
    {
        get
        {
            StartServer();
            var addresses = Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses ?? [];
            return addresses.First(a => !a.EndsWith($":{GrpcPort}", StringComparison.Ordinal))
                .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = Issuer,
            }));
        builder.ConfigureServices(services =>
        {
            TestWebApplicationFactory.ReplaceDbContext<AuthorizationDbContext>(services, _dbName);

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration { Issuer = Issuer });
                o.TokenValidationParameters.IssuerSigningKey = SigningKey;
                o.TokenValidationParameters.ValidIssuer = Issuer;
            });
        });
    }

    // テスト用 IdP の代わりに JWT を発行する。realm_access.roles は KeycloakRolesClaimsTransformation が
    // ClaimTypes.Role へ展開する（実 Keycloak トークンと同じ形）。
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
