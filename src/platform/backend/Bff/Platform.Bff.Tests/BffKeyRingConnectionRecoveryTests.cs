using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Bff.Foundation.Endpoints;
using Platform.Bff.Foundation.Session;
using StackExchange.Redis;
using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Platform.Bff.Tests;

// NFR, NFR-07, NFR-18, ADR-0032, IADR-0251 決定 5, [[IADR-0633]] (#2242):
// **鍵リングの Valkey 接続は初回の失敗を覚えない。** 起動時に 1 度つながらなかっただけで、そのプロセスのログインが
// 全部 500 になっていた（#2232・#2241 の e2e-live。Ready なのに `/bff/auth/login` が 500）。
//
// 本番の配線（AddBffSession の遅延・RedisXmlRepository・OIDC の challenge が nonce の cookie を DataProtection で保護する）のまま、
// 差し替えるのは「接続を作る関数」（SessionStoreConnect）だけである: 1 回目は RedisConnectionException、2 回目からは偽の Valkey。
public class BffKeyRingConnectionRecoveryTests
{
    private const string AuthorizationEndpoint = "https://keycloak.test/realms/platform/protocol/openid-connect/auth";

    // 受け入れ基準 1: 最初の接続が失敗しても、Valkey が回復した後の login は作り直さずに 302 を返す。
    [Fact]
    public async Task 最初の接続が失敗しても_回復した後のloginは302を返す()
    {
        var valkey = new InMemoryRedisLists();
        var reachable = false;
        var failed = 0;
        var connected = 0;
        IConnectionMultiplexer Connect(ConfigurationOptions _)
        {
            if (!reachable)
            {
                failed++;
                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "試験: Valkey へつながらない");
            }
            connected++;
            return valkey.Multiplexer;
        }

        using var host = await StartAsync(Connect);
        var client = host.GetTestServer().CreateClient();

        // 前提の再現: Valkey へつながらない間、login は 500（鍵リングを読めず nonce の cookie を保護できない）。
        var whileDown = await client.GetAsync("/bff/auth/login", TestContext.Current.CancellationToken);
        whileDown.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "つながらない間は鍵リングを使えない");
        failed.Should().BeGreaterThan(0, "接続を試みて失敗している（失敗を経ていなければ回復の試験にならない）");

        // Valkey が回復する（プロセスは作り直さない）。
        reachable = true;
        var afterRecovery = await client.GetAsync("/bff/auth/login", TestContext.Current.CancellationToken);
        afterRecovery.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "失敗を覚えない遅延なら、回復した後の login は認可サーバへの 302 になる（覚えると 500 のまま）");
        afterRecovery.Headers.Location!.GetLeftPart(UriPartial.Path).Should().Be(AuthorizationEndpoint);

        var again = await client.GetAsync("/bff/auth/login", TestContext.Current.CancellationToken);
        again.StatusCode.Should().Be(HttpStatusCode.Redirect);
        connected.Should().Be(1, "成功した接続は使い回す（要求ごとに接続し直さない）");
    }

    // 受け入れ基準 2（振る舞い）: 失敗を覚えない遅延そのもの。
    [Fact]
    public void SessionStoreConnectionの遅延は失敗を覚えず_成功は使い回す()
    {
        var valkey = new InMemoryRedisLists();
        var calls = 0;
        var lazy = SessionStoreConnection.Lazy(() => ++calls == 1
            ? throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "試験")
            : valkey.Multiplexer);

        var firstUse = () => lazy.Value;
        firstUse.Should().Throw<RedisConnectionException>();
        lazy.Value.Should().BeSameAs(valkey.Multiplexer);
        lazy.Value.Should().BeSameAs(valkey.Multiplexer);
        calls.Should().Be(2);
    }

    // 受け入れ基準 2（方針の一致）: 鍵リングとヘルスチェックは同じ置き場（SessionStoreConnection）から遅延を作る。
    // 🔴 片方だけ既定の Lazy（例外を覚える）へ戻すと、#2242 の「Ready なのにログインは全部 500」へ戻る。
    [Fact]
    public void Platform_Bffの本番コードでIConnectionMultiplexerのLazyを作るのはSessionStoreConnectionだけ()
    {
        var bff = Path.Combine(RepoRoot(), "src", "platform", "backend", "Bff", "Platform.Bff");
        var lazyOfMultiplexer = new Regex(@"new\s+Lazy\s*<\s*(StackExchange\.Redis\.)?IConnectionMultiplexer\s*>|new\s*\(\s*connect\s*,\s*LazyThreadSafetyMode");
        var offenders = Directory.EnumerateFiles(bff, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => lazyOfMultiplexer.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetFileName(f))
            .ToList();
        offenders.Should().BeEquivalentTo(["SessionStoreConnection.cs"]);

        File.ReadAllText(Path.Combine(bff, "Program.cs")).Should().Contain("SessionStoreConnection.Lazy(",
            "ヘルスチェックの接続も同じ遅延から作る");
        File.ReadAllText(Path.Combine(bff, "Foundation", "Session", "BffSessionExtensions.cs"))
            .Should().Contain("SessionStoreConnection.Lazy(", "鍵リングの接続も同じ遅延から作る");
    }

    private static async Task<IHost> StartAsync(SessionStoreConnect connect)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BffSession:ClientSecret"] = "test-secret" })
            .Build();
        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    // 接続を作る関数だけを差し替える（AddBffSession は TryAdd なので、先に置いたものが残る）。
                    services.AddSingleton(connect);
                    services.AddRouting();
                    services.AddAuthorization();
                    services.AddLogging();
                    services.AddBffSession(config);
                    // セッションの実体（チケット）はメモリ。鍵リング（DataProtection）は本番の配線のまま Valkey を引く。
                    services.AddSingleton<IDistributedCache>(new MemoryDistributedCache(
                        Options.Create(new MemoryDistributedCacheOptions())));
                    // OIDC メタデータ → 静的構成（実 Keycloak はこの環境に無い）。
                    services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                    {
                        var configuration = new OpenIdConnectConfiguration
                        {
                            Issuer = "https://keycloak.test/realms/platform",
                            AuthorizationEndpoint = AuthorizationEndpoint,
                        };
                        o.Configuration = configuration;
                        o.ConfigurationManager =
                            new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    });
                })
                .Configure(app =>
                {
                    // Kestrel と同じく、処理されない例外は 500 にする（TestServer は例外を呼び出し側へ投げ返すため）。
                    app.Use(async (ctx, next) =>
                    {
                        try
                        {
                            await next(ctx);
                        }
                        catch (Exception) when (!ctx.Response.HasStarted)
                        {
                            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        }
                    });
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapAuthBffEndpoints());
                }))
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "docker-compose.yml")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("リポジトリルート（deploy/docker-compose.yml を持つ）を解決できなかった。");
    }
}
