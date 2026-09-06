using System.Text;
using DashboardService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DashboardService.Tests.Grpc;

// FR-10, FR-17, FR-18, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]], [[IADR-0408]] (#1255):
// 観測値の受け口の gRPC 面のための器。**DocumentService / AuthorizationService の同名の器と同型**である。
//
// 🔴 **TestServer ではなく実 Kestrel で起こす。** TestServer は in-memory であり、h2c のポートが
// 実際に bind され、**HTTP/1.1 のポートが消えていない**ことを観測できない
// （`AddPlatformGrpcListener` の 🔴 を参照）。gRPC 用ポートは `GrpcTestConfiguration` が
// 空きポートを選んで `Grpc__Port` として渡す。
//
// 認証は実 IdP を持たないので、JwtBearer の検証鍵と issuer をテスト用の対称鍵へ差し替える。
// 🔴 **`TestAuthHandler` は使わない** —— 既定スキームを差し替えると `ServiceCaller` ポリシーが
// 「本物の JwtBearer を通ったか」を測れなくなり、**利用者トークンの拒否**という
// 本面の唯一の統制が測定できない（`TestWebApplicationFactory` はそちらを使うが、
// あれは REST の集計面〔ロール限定〕を測るための器である）。
public sealed class GrpcKestrelFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://test-issuer/realms/platform";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("grpc-s2s-test-signing-key-0123456789abcdef-0123456789"));

    private readonly string _dbName = $"DashboardGrpc_{Guid.NewGuid()}";

    // ポートは GrpcTestConfiguration（環境変数）が決める。ConfigureAppConfiguration では間に合わない。
    public int GrpcPort => GrpcTestConfiguration.GrpcPort;

    public GrpcKestrelFactory() => UseKestrel();

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
                // FR-10, ADR-0072 決定 3 (#1198): 背景の掃除は止める（投入した行が試験の途中で消え得る）。
                ["UsageRetention:Enabled"] = "false",
            }));
        builder.ConfigureServices(services =>
        {
            ReplaceDbContext<DashboardDbContext>(services, _dbName);

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration { Issuer = Issuer });
                o.TokenValidationParameters.IssuerSigningKey = SigningKey;
                o.TokenValidationParameters.ValidIssuer = Issuer;
            });
        });
    }

    private static void ReplaceDbContext<TContext>(IServiceCollection services, string dbName)
        where TContext : DbContext
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<TContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?.Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(TContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<TContext>(opt => opt.UseInMemoryDatabase(dbName));
    }

    // テスト用 IdP の代わりに JWT を発行する。realm_access.roles は
    // KeycloakRolesClaimsTransformation が ClaimTypes.Role へ展開する（実 Keycloak トークンと同じ形）。
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
