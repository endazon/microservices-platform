using System.Text;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Platform.Shared.Contracts.Dtos;
using Wolverine;

namespace GraphService.Tests.Grpc;

// FR-04, FR-05, FR-17, UC-10, NFR-09, NFR-16, ADR-0029, ADR-0034, ADR-0075, 計画 ADR-0086,
// [[IADR-0379]], [[IADR-0410]] (#1255): 近傍展開の gRPC 面のための器。
// **DocumentService / AuthorizationService の同名の器と同型**である。
//
// 🔴 **TestServer ではなく実 Kestrel で起こす。** TestServer は in-memory であり、h2c のポートが
// 実際に bind され、**HTTP/1.1 のポートが消えていない**ことを観測できない
// （`AddPlatformGrpcListener` の 🔴 を参照）。gRPC 用ポートは `GrpcTestConfiguration` が
// 空きポートを選んで `Grpc__Port` として渡す。
//
// 認証は実 IdP を持たないので、JwtBearer の検証鍵と issuer をテスト用の対称鍵へ差し替える。
// **TestAuthHandler は使わない** —— s2s の検証は本物の JwtBearer パイプライン
// （`AddPlatformAuth` ＋ `KeycloakRolesClaimsTransformation`）を通してこそ意味がある。
//
// 🔴 **ABAC スコープの解決だけを差し替える**（認可サービスへの実通信を持たないため）。
// 差し替えるのは**後段（`AuthzScope/Resolve`）への往復**であって**判定の位置ではない** ——
// 本器は `ResolveForUserAsync` に渡された利用者文脈を記録し、試験が
// 「**呼び出し先が本文の文脈で自分の判定を行っている**」ことを観測できるようにする。
public sealed class GrpcKestrelFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://test-issuer/realms/platform";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("grpc-s2s-test-signing-key-0123456789abcdef-0123456789"));

    private readonly string _dbName = $"GraphGrpc_{Guid.NewGuid()}";

    // 🔴 **本文で運ばれた利用者文脈から答えを決める。** 既定は「利用者が分かれば全許可」であり、
    // 試験は `ScopeFor` を差し替えて拒否側の腕を作る（陽性・陰性を対で置くため）。
    public Func<GraphUserContext, string, AccessScopeResponse> ScopeFor { get; set; } =
        (user, _) => new AccessScopeResponse(user.UserId, [], true);

    /// <summary>`ResolveForUserAsync` が受け取った利用者文脈（判定の位置の観測点）。</summary>
    public List<(GraphUserContext User, string Action)> ResolvedFor { get; } = [];

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
                ["Services:AuthorizationService"] = "http://localhost/authz",
            }));
        builder.ConfigureServices(services =>
        {
            ReplaceDbContext<GraphDbContext>(services, _dbName);

            services.RemoveAll<IGraphAccessResolver>();
            services.AddScoped<IGraphAccessResolver>(_ => new RecordingAccessResolver(this));

            // 🔴 これが無いとテストホストの起動が実ブローカへの接続を試みてハングする
            // （`TestWebApplicationFactory` と同じ作法）。
            services.DisableAllExternalWolverineTransports();

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration { Issuer = Issuer });
                o.TokenValidationParameters.IssuerSigningKey = SigningKey;
                o.TokenValidationParameters.ValidIssuer = Issuer;
            });
        });
    }

    public async Task SeedAsync(Func<GraphDbContext, Task> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
        await seed(db);
        await db.SaveChangesAsync();
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

    // 🔴 **本文の利用者文脈で答えを決め、渡された文脈を記録する。**
    // 記録があることで、試験は「呼び出し先が**自分で**判定していること」を観測できる ——
    // 呼び出し元が解決したスコープを信じているなら、ここは 1 度も呼ばれない。
    private sealed class RecordingAccessResolver(GrpcKestrelFactory owner) : IGraphAccessResolver
    {
        public Task<AccessScopeResponse> ResolveAsync(
            HttpContext ctx, string action, CancellationToken ct = default)
            => ResolveForUserAsync(GraphUserContext.FromHttpContext(ctx), action, ct);

        public Task<AccessScopeResponse> ResolveForUserAsync(
            GraphUserContext user, string action, CancellationToken ct = default)
        {
            owner.ResolvedFor.Add((user, action));
            return Task.FromResult(owner.ScopeFor(user, action));
        }
    }
}
