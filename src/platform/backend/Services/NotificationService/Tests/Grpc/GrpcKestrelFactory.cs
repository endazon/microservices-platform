using System.Text;
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
using NotificationService.Infrastructure.Persistence;

namespace NotificationService.Tests.Grpc;

// FR-22, NFR-09, NFR-16, ADR-0004, ADR-0029, ADR-0075, [[IADR-0379]], [[IADR-0417]] 決定 8,
// [[IADR-0419]] (#1255): 通知の受け口の gRPC 面のための器。
// **AuthorizationService / DocumentService / RetrievalService の同名の器と同型**である。
//
// 🔴 **TestServer ではなく実 Kestrel で起こす。** TestServer は in-memory であり、h2c のポートが
// 実際に bind され、**HTTP/1.1 のポートが消えていない**ことを観測できない
// （`AddPlatformGrpcListener` の 🔴 を参照）。gRPC 用ポートは `GrpcTestConfiguration` が
// 空きポートを選んで `Grpc__Port` として渡す。
//
// 🔴 **認証は本物の JwtBearer パイプラインを通す**（`TestAuthHandler` は使わない）。
// 本サービスの既存の器は主体をヘッダで差し替えるが、**s2s の検証はそれでは測れない** ——
// realm ロール `platform-service` を持つ主体だけが面を開けることを見たいので、
// 検証鍵と issuer をテスト用の対称鍵へ差し替えたうえで実際にトークンを検証させる。
//
// 🔴 **DB は差し替えるが、受理の判断（検証・重複判定・永続化）は差し替えない。**
// 面が「同じ本体を通っている」ことは、**実際に台帳へ 1 件積まれたこと**でしか観測できない
// （呼び出しが 200 で返るだけの器では、面が本体を通らない実装でも緑になる）。
public sealed class GrpcKestrelFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://test-issuer/realms/platform";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("grpc-s2s-test-signing-key-0123456789abcdef-0123456789"));

    private readonly string _dbName = $"NotificationGrpc_{Guid.NewGuid()}";

    // ポートは GrpcTestConfiguration（環境変数）が決める。ConfigureAppConfiguration では間に合わない。
    public int GrpcPort => GrpcTestConfiguration.GrpcPort;

    public GrpcKestrelFactory() => UseKestrel();

    public string GrpcAddress => $"http://127.0.0.1:{GrpcPort}";

    // HTTP/1.1 側（REST・受け口・/health/*）の実アドレス。gRPC を有効にしてもこちらが残っていることの証明に使う。
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

    public NotificationDbContext NewDbContext()
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = Issuer,
                // 背景の送出・掃除は止める（時刻に依存した検証が背景処理で揺れないようにする）。
                ["Notification:MaintenanceEnabled"] = "false",
            }));
        builder.ConfigureServices(services =>
        {
            TestWebApplicationFactory.ReplaceDbContext<NotificationDbContext>(services, _dbName);

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration { Issuer = Issuer });
                o.TokenValidationParameters.IssuerSigningKey = SigningKey;
                o.TokenValidationParameters.ValidIssuer = Issuer;
            });
        });
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
