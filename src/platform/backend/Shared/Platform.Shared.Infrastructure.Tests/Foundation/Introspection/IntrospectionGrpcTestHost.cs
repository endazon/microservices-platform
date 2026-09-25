using System.Net;
using AwesomeAssertions;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Introspection;

// FR-15, NFR-09, NFR-16, ADR-0029, IADR-0379 決定 3・4, IADR-0462 (#1514): 自己申告の REST 面と gRPC 面を
// **本番と同じ共通部品**（`AddPlatformAuth` / `AddPlatformIntrospection` / `UsePlatformMiddleware` /
// `MapPlatformIntrospection`）で張った最小のホスト。
//
// 🔴 **待受はループバック（127.0.0.1）に限る。** HTTP/1.1（REST）と HTTP/2 専用（h2c）の 2 本を
// `IPAddress.Loopback` の動的ポートへ開き、起動直後に全待受アドレスがループバックであることを
// 確かめる（1 つでも外れたら停止して落とす。各サービスの `GrpcTestConfiguration.LoopbackOnlyGuard` と同じ主張）。
// 0.0.0.0 では待ち受けない。
//
// 認証は実 IdP を持たないので、JwtBearer の検証鍵と issuer をテスト用の対称鍵へ差し替える
// （各サービスの `GrpcKestrelFactory` と同型。`TestAuthHandler` で既定スキームを差し替えると、
// `ServiceCaller` が本物の JwtBearer を通ったかを測れなくなる）。
internal sealed class IntrospectionGrpcTestHost : IAsyncDisposable
{
    public const string Issuer = "https://test-issuer/realms/platform";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("introspection-grpc-test-signing-key-0123456789abcdef"));

    private readonly WebApplication _app;

    private IntrospectionGrpcTestHost(WebApplication app, string httpAddress, string grpcAddress)
    {
        _app = app;
        HttpAddress = httpAddress;
        GrpcAddress = grpcAddress;
    }

    // REST（HTTP/1.1）側のベース URL。
    public string HttpAddress { get; }

    // gRPC（h2c）側のアドレス。
    public string GrpcAddress { get; }

    // `report` を渡せば、組み立てた申告の代わりにそれを DI へ置く（空の service 名など、
    // `IntrospectionBuilder` では作れない申告を返させるため）。
    public static async Task<IntrospectionGrpcTestHost> StartAsync(
        ServiceIntrospectionDto? report = null, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = Issuer,
        });
        ListenOptions? http = null;
        ListenOptions? h2c = null;
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0, o => { o.Protocols = HttpProtocols.Http1; http = o; });
            kestrel.Listen(IPAddress.Loopback, 0, o => { o.Protocols = HttpProtocols.Http2; h2c = o; });
        });

        builder.Services.AddPlatformAuth(builder.Configuration);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration { Issuer = Issuer });
            o.TokenValidationParameters.IssuerSigningKey = SigningKey;
            o.TokenValidationParameters.ValidIssuer = Issuer;
        });

        builder.Services.AddPlatformIntrospection("probe-service", new PipelineOptions(), i => i
            .AddPort("vector-store", "QdrantVectorStore", "qdrant:6334")
            .AddPort("llm", "NullLlm")
            .AddPort("wiki-sync", "WikiJs", string.Empty)
            .AddConnector("filesystem", true)
            .AddConnector("saas", false));
        if (report is not null)
            builder.Services.AddSingleton(report);

        var app = builder.Build();
        app.UsePlatformMiddleware();
        app.MapPlatformIntrospection();
        await app.StartAsync(ct);

        var addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
        var open = addresses.Where(a => !IsLoopback(a)).ToList();
        if (open.Count > 0)
        {
            await app.StopAsync(ct);
            throw new InvalidOperationException(
                $"試験の待受がループバック以外に開いた: {string.Join(", ", open)}");
        }

        addresses.Should().HaveCount(2, "HTTP/1.1 と h2c の 2 本だけを開く");
        // bind 後の実ポートは ListenOptions.IPEndPoint に書き戻される。
        return new IntrospectionGrpcTestHost(app,
            $"http://127.0.0.1:{http!.IPEndPoint!.Port}",
            $"http://127.0.0.1:{h2c!.IPEndPoint!.Port}");
    }

    private static bool IsLoopback(string address)
    {
        var host = BindingAddress.Parse(address).Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
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

    public static string ServiceToken() =>
        IssueToken("service-account-bff", [PlatformAuthPolicies.ServiceRole]);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

// 固定のトークンを返す s2s トークン発行側（IdP を持たない試験用）。
internal sealed class FixedTokenProvider(string token) : IServiceTokenProvider
{
    public ValueTask<string> GetTokenAsync(CancellationToken ct) => ValueTask.FromResult(token);
}
