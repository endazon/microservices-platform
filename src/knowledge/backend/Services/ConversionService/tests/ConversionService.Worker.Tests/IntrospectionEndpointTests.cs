using System.Net;
using System.Net.Http.Json;
using ConversionService.Worker.Foundation.Persistence;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConversionService.Worker.Tests;

// FR-15, IADR-0029 (#142): conversion ワーカーの自己申告エンドポイント（/internal/introspection）が
// メッシュ内部限定で到達でき、担当段（convert）を申告することを検証する。これにより convert 段が
// ドリフト検出で Unverifiable でなくなる。
public class IntrospectionEndpointTests : IClassFixture<IntrospectionEndpointTests.Factory>
{
    private readonly Factory _factory;

    public IntrospectionEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Introspection_endpoint_reports_convert_step()
    {
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/internal/introspection");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await res.Content.ReadFromJsonAsync<ServiceIntrospectionDto>();
        report.Should().NotBeNull();
        report!.Service.Should().Be("conversion-service");
        report.Steps.Should().ContainSingle(s => s.Name == "convert")
            .Which.Enabled.Should().BeTrue();
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317"
                }));
            builder.ConfigureServices(services =>
            {
                // IADR-0043: 実 Postgres 接続（起動時 MigrateAsync）を避けるため DbContext を InMemory へ差し替える
                // （InMemory は非リレーショナルのため MigrateAsync はスキップされる）。
                services.ReplaceDbContextWithInMemory<ConversionJobDbContext>("IntrospectionTest");

                // 実 RabbitMQ 接続を避けるため MassTransit をテストハーネスへ差し替える。
                services.RemoveAll<IBusControl>();
                services.AddMassTransitTestHarness();
            });
        }
    }
}
