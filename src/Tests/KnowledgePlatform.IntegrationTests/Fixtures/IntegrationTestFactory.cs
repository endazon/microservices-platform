using DocumentService.Api.Infrastructure;
using DataSourceService.Api.Infrastructure;
using AuthorizationService.Api.Infrastructure;
using WikiService.Api.Infrastructure;
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

            // MassTransit: RabbitMQ があれば実コネクション、なければ InMemory
            services.RemoveAll<IBusControl>();
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
                services.AddMassTransitTestHarness(x => RegisterConsumers(x));
            }

            AdditionalServices(services);
        });
    }

    protected virtual void RegisterConsumers(IBusRegistrationConfigurator x) { }
    protected virtual void AdditionalServices(IServiceCollection services) { }

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
}

public sealed class WikiServiceFactory : IntegrationTestFactoryBase<
    global::WikiService.Api.WikiServiceTestMarker, WikiDbContext>
{
    public WikiServiceFactory(PostgresFixture pg, RabbitMqFixture rabbit) : base(pg, rabbit) { }
    protected override void RegisterConsumers(IBusRegistrationConfigurator x)
        => x.AddConsumer<global::WikiService.Api.Consumers.DocumentSyncConsumer>();
}
