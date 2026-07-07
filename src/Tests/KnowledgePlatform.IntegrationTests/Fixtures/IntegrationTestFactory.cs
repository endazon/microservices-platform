using DocumentService.Api.Foundation.Persistence;
using DataSourceService.Api.Foundation.Persistence;
using AuthorizationService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KnowledgePlatform.IntegrationTests.Fixtures;

// UC-03, UC-04, UC-05: 統合テスト用 WebApplicationFactory 基底クラス
// TestContainers の Postgres/RabbitMQ を使いサービスを実際に起動する
public abstract class IntegrationTestFactoryBase<TProgram, TDbContext> : WebApplicationFactory<TProgram>
    where TProgram : class
    where TDbContext : DbContext
{
    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture? _rabbit;

    protected IntegrationTestFactoryBase(PostgresFixture postgres, RabbitMqFixture? rabbit = null)
    {
        _postgres = postgres;
        _rabbit = rabbit;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Integration");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var overrides = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _postgres.ConnectionString ?? "Host=localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test"
            };
            if (_rabbit?.ConnectionString is { } rabbitCs)
                overrides["RabbitMq:ConnectionString"] = rabbitCs;
            cfg.AddInMemoryCollection(overrides);
        });

        builder.ConfigureServices(services =>
        {
            // DbContext: Npgsql で TestContainers Postgres を使う
            ReplaceDbContextWithNpgsql<TDbContext>(services, _postgres.ConnectionString ?? "Host=localhost");

            // MassTransit: Program.cs が AddMassTransit() 済みのためアセンブリ単位で全削除してから再登録
            // AddMassTransit() is idempotent-blocked by IBus presence check; remove all MT services first
            RemoveAllMassTransitServices(services);

            if (_rabbit is not null)
            {
                services.AddMassTransit(x =>
                {
                    RegisterConsumers(x);
                    x.UsingRabbitMq((ctx, cfg) =>
                    {
                        cfg.Host(_rabbit.ConnectionString);
                        cfg.ConfigureEndpoints(ctx);
                    });
                });
            }
            else
            {
                services.AddMassTransit(x =>
                {
                    RegisterConsumers(x);
                    x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
                });
            }

            // Issue #33: Bus 起動レース対策。既定では MassTransitHostedService が Bus を
            // バックグラウンド起動するため、CreateClient() 直後の Publish が Consumer の
            // キューバインド完了前に走り、メッセージが破棄され得る。WaitUntilStarted=true で
            // レシーブエンドポイントのバインド完了までホスト起動を待機させ、購読確立後に
            // Publish されることを保証する。
            services.AddOptions<MassTransitHostOptions>().Configure(o =>
            {
                o.WaitUntilStarted = true;
                o.StartTimeout = TimeSpan.FromSeconds(30);
                o.StopTimeout = TimeSpan.FromSeconds(10);
            });

            AdditionalServices(services);
        });
    }

    protected virtual void RegisterConsumers(IBusRegistrationConfigurator x) { }
    protected virtual void AdditionalServices(IServiceCollection services) { }

    private static void RemoveAllMassTransitServices(IServiceCollection services)
    {
        var toRemove = services
            .Where(d =>
                IsFromMassTransitAssembly(d.ServiceType) ||
                IsFromMassTransitAssembly(d.ImplementationType) ||
                (d.ImplementationInstance is not null &&
                 IsFromMassTransitAssembly(d.ImplementationInstance.GetType())))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);
    }

    private static bool IsFromMassTransitAssembly(Type? type)
    {
        if (type is null) return false;
        var name = type.Assembly.GetName().Name;
        return name is not null && name.StartsWith("MassTransit", StringComparison.Ordinal);
    }

    private static void ReplaceDbContextWithNpgsql<T>(IServiceCollection services, string connStr)
        where T : DbContext
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<T>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?
                                .Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(T)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<T>(opt => opt.UseNpgsql(connStr));
    }
}

// ── 各サービス固有ファクトリ ────────────────────────────

// global:: でローカル namespace（KnowledgePlatform.IntegrationTests.*）を隠さないようにする
public sealed class DocumentServiceFactory : IntegrationTestFactoryBase<
    global::DocumentService.Api.DocumentServiceTestMarker, DocumentDbContext>
{
    public DocumentServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
    // FR-01, UC-04: DocumentNormalized 購読 Consumer
    protected override void RegisterConsumers(IBusRegistrationConfigurator x)
        => x.AddConsumer<global::DocumentService.Api.Composable.Steps.DocumentNormalizedConsumer>();
}

public sealed class DataSourceServiceFactory : IntegrationTestFactoryBase<
    global::DataSourceService.Api.DataSourceServiceTestMarker, DataSourceDbContext>
{
    public DataSourceServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
}

public sealed class AuthorizationServiceFactory : IntegrationTestFactoryBase<
    global::AuthorizationService.Api.AuthorizationServiceTestMarker, AuthorizationDbContext>
{
    public AuthorizationServiceFactory(PostgresFixture pg) : base(pg, null) { }

    // FR-09, ADR-0004: 管理系エンドポイント（/authz/policies 等）は AdminOnly を要求する。
    // 実 Keycloak が無い統合環境では TestAuthHandler で platform-admin として認証し、
    // DB 挙動（ポリシー登録→スコープ解決 等）を検証する。既定認証スキームを差し替える。
    protected override void AdditionalServices(IServiceCollection services)
    {
        services.AddAuthentication(IntegrationTestAuthHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, IntegrationTestAuthHandler>(
                IntegrationTestAuthHandler.SchemeName, _ => { });
    }
}

public sealed class WikiServiceFactory : IntegrationTestFactoryBase<
    global::WikiService.Api.WikiServiceTestMarker, WikiDbContext>
{
    public WikiServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
    protected override void RegisterConsumers(IBusRegistrationConfigurator x)
        => x.AddConsumer<global::WikiService.Api.Composable.Steps.DocumentSyncConsumer>();
}
