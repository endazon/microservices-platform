using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using IngestionService.Worker.Infrastructure.ExternalServices;
using Platform.Shared.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace IngestionService.Worker.Tests;

// FR-15, IADR-0029 (#142): ingestion ワーカーの自己申告エンドポイント（/internal/introspection）が
// メッシュ内部限定で到達でき、担当段（ingest）を申告することを検証する。これにより ingest 段が
// ドリフト検出で Unverifiable でなくなる。
public class IntrospectionEndpointTests : IClassFixture<IntrospectionEndpointTests.Factory>
{
    private readonly Factory _factory;

    public IntrospectionEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Introspection_endpoint_reports_ingest_step()
    {
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/internal/introspection", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await res.Content.ReadFromJsonAsync<ServiceIntrospectionDto>(TestContext.Current.CancellationToken);
        report.Should().NotBeNull();
        report!.Service.Should().Be("ingestion-service");
        report.Steps.Should().ContainSingle(s => s.Name == "ingest")
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
                    ["Otlp:Endpoint"] = "http://localhost:4317"
                }));
            builder.ConfigureServices(services =>
            {
                // 実 RabbitMQ 接続を避けるため MassTransit をテストハーネスへ差し替える。
                services.RemoveAll<IBusControl>();
                services.AddMassTransitTestHarness();

                // ADR-0027 / E3b: ingest 段の購読は Wolverine へ移った。
                // 🔴 **これが無いとテストが約 135 秒ハングする** —— Program.cs が UseWolverine +
                // UseRabbitMq を呼ぶため、テストホストの起動が実ブローカへの接続を試みる
                // （E1 の DataSourceService.Tests と同じ作法）。
                services.DisableAllExternalWolverineTransports();

                // 起動時の Qdrant 接続（コレクション作成）を避けるためブートストラップを外す
                // （自己申告エンドポイントの検証に Qdrant は不要）。
                foreach (var d in services
                             .Where(d => d.ImplementationType == typeof(QdrantBootstrapHostedService))
                             .ToList())
                {
                    services.Remove(d);
                }
            });
        }
    }
}
