using System.Text;
using Anthropic.SDK;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LlmGateway.Tests.Features.Embeddings.Embed;

// FR-02, FR-03, FR-05, NFR-09, NFR-16, ADR-0016, ADR-0029, ADR-0075, IADR-0379, IADR-0397 (#1255):
// 埋め込みの gRPC 面の器。AuthorizationService の参照実装（#1201）と同型である。
//
// 🔴 **TestServer ではなく実 Kestrel で起こす。** TestServer は in-memory であり、h2c（TLS 無し HTTP/2）の
// ポートが実際に bind され、HTTP/1.1 のポートが**消えていない**ことを観測できない
// （AddPlatformGrpcListener の 🔴 を参照）。h2c 用ポートは GrpcTestConfiguration が環境変数で与える。
//
// 認証は実 IdP を持たないので JwtBearer の検証鍵と issuer をテスト用の対称鍵へ差し替える。
// TestAuthHandler は使わない —— s2s の検証は**本物の JwtBearer パイプライン**
// （AddPlatformAuth ＋ KeycloakRolesClaimsTransformation）を通してこそ意味がある。
//
// 埋め込みプロバイダは外部 API を持たないスタブへ差し替える（TestWebApplicationFactory と同じ形）。
// **ルータ（EmbeddingRouter）と越境判定は差し替えない** —— そこが本試験の観測対象だからである。
public sealed class GrpcKestrelFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://test-issuer/realms/platform";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("grpc-s2s-test-signing-key-0123456789abcdef-0123456789"));

    // ポートは GrpcTestConfiguration（環境変数）が決める。ConfigureAppConfiguration では間に合わない。
    public static int GrpcPort => GrpcTestConfiguration.GrpcPort;

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
                ["Llm:ApiKey"] = "test-key",
                ["Llm:Model"] = "claude-opus-5",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = Issuer,
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AnthropicClient>();
            services.RemoveAll<ILlmProvider>();

            // 外部 API 基盤を持たないので埋め込みプロバイダだけスタブへ差し替える
            // （要求次元どおりのベクトルを返す）。ルータと越境判定は本物のままである。
            services.RemoveAll<IEmbeddingProvider>();
            services.AddKeyedSingleton<IEmbeddingProvider, StubEmbeddingProvider>("voyage");
            services.AddKeyedSingleton<IEmbeddingProvider, StubEmbeddingProvider>("selfhosted-embedding");
            services.AddKeyedSingleton<IEmbeddingProvider, StubEmbeddingProvider>("deterministic-embedding");

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

// 要求次元どおりの「本物らしい」ベクトルを返すスタブ（ゼロベクトルだと次元照合しか観測できない）。
file sealed class StubEmbeddingProvider : IEmbeddingProvider
{
    public Task<float[]> EmbedAsync(
        string text, string model, int dimensions, EmbeddingRoutePurpose purpose, CancellationToken ct = default)
    {
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
            vector[i] = (i + 1) * 0.001f;
        return Task.FromResult(vector);
    }
}
