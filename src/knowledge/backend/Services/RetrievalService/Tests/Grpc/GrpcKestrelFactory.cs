using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Platform.Shared.Contracts.Dtos;
using Qdrant.Client;
using RetrievalService.Domain.Ports;
using RetrievalService.Infrastructure.ExternalServices;
using Wolverine;

namespace RetrievalService.Tests.Grpc;

// FR-04, FR-05, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]], [[IADR-0402]],
// [[IADR-0416]], [[IADR-0417]] (#1255): 権限内属性値の照会の gRPC 面のための器。
// **DocumentService / GraphService / AuthorizationService の同名の器と同型**である。
//
// 🔴 **TestServer ではなく実 Kestrel で起こす。** TestServer は in-memory であり、h2c のポートが
// 実際に bind され、**HTTP/1.1 のポートが消えていない**ことを観測できない
// （`AddPlatformGrpcListener` の 🔴 を参照）。gRPC 用ポートは `GrpcTestConfiguration` が
// 空きポートを選んで `Grpc__Port` として渡す。
//
// 認証は実 IdP を持たないので、JwtBearer の検証鍵と issuer をテスト用の対称鍵へ差し替える。
// **s2s の検証は本物の JwtBearer パイプラインを通してこそ意味がある。**
//
// 🔴 **ABAC の解決だけを差し替える**（認可サービスへの実通信を持たないため）。
// 差し替えるのは**後段への往復**であって**判定の位置ではない** ——
// 本器は `ResolveForUserAsync` に渡された利用者文脈を記録し、試験が
// 「**呼び出し先が本文の文脈で自分の判定を行っている**」ことを観測できるようにする。
public sealed class GrpcKestrelFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://test-issuer/realms/platform";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("grpc-s2s-test-signing-key-0123456789abcdef-0123456789"));

    /// <summary>受け口が**自分で引く**許可スコープ。狭めると「主張が超えられない」ことを測れる。</summary>
    public AccessScopeResponse Authoritative { get; set; } = new("test-user", [], Granted: true);

    /// <summary>`ResolveForUserAsync` が受け取った利用者文脈（判定の位置の観測点）。</summary>
    public List<(string UserId, IReadOnlyDictionary<string, string> Attributes)> ResolvedFor { get; } = [];

    /// <summary>試験が索引へ入れるチャンクの置き場（器の寿命で共有）。</summary>
    public InMemoryVectorStore Index { get; } = new();

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
            return addresses.First(a => !a.EndsWith($":{GrpcPort}", StringComparison.Ordinal));
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
                ["Qdrant:Host"] = "localhost",
                ["Qdrant:Port"] = "6334",
                ["Services:LlmGateway"] = "http://localhost:5007",
            }));
        builder.ConfigureServices(services =>
        {
            // NFR-16 (#1509): 起動直後に待受がループバックだけであることを確かめる（HttpAddress を読まない試験でも効く）。
            services.AddHostedService<GrpcTestConfiguration.LoopbackOnlyGuard>();

            services.RemoveAll<QdrantClient>();
            services.RemoveAll<IVectorStore>();
            services.AddSingleton<IVectorStore>(Index);

            services.RemoveAll<IEmbeddingService>();
            services.AddSingleton<IEmbeddingService, StubEmbeddingService>();

            // 🔴 これが無いとテストホストの起動が実ブローカへの接続を試みてハングする。
            services.DisableAllExternalWolverineTransports();

            services.RemoveAll<ISearchAccessResolver>();
            services.AddSingleton<ISearchAccessResolver>(new RecordingAccessResolver(this));

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
    // `azp`（authorized party）を与えると、実 Keycloak と同じくクライアント識別のクレームが付く（#1635）。
    // `withUsername: false` は `profile` スコープを持たない機械クライアントの実形（`preferred_username` が無い）。
    // realm の `aianalysis-service` は既定スコープが `roles` だけなので、実トークンはこちらの形である。
    public static string IssueToken(
        string subject, IEnumerable<string> realmRoles, string? azp = null, bool withUsername = true)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["realm_access"] = new Dictionary<string, object> { ["roles"] = realmRoles.ToArray() },
        };
        if (withUsername)
            claims["preferred_username"] = subject;
        if (azp is not null)
            claims["azp"] = azp;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = claims,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    // 🔴 **本文で運ばれた利用者文脈を記録する。**
    // 記録があることで、試験は「呼び出し先が**自分で**判定していること」を観測できる ——
    // 呼び出し元が解決したスコープを信じているなら、ここは 1 度も呼ばれない。
    private sealed class RecordingAccessResolver(GrpcKestrelFactory owner) : ISearchAccessResolver
    {
        public Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, CancellationToken ct = default)
            => Task.FromResult(owner.Authoritative);

        public Task<AccessScopeResponse> ResolveForUserAsync(
            string userId, IReadOnlyDictionary<string, string> attributes, CancellationToken ct = default)
        {
            owner.ResolvedFor.Add((userId, attributes));
            return Task.FromResult(owner.Authoritative);
        }
    }
}
