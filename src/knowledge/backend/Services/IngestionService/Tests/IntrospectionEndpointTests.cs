using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using IngestionService.Infrastructure.ExternalServices;
using Platform.Shared.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.Introspection.V1;

namespace IngestionService.Tests;

// FR-15, IADR-0029 (#142): ingestion ワーカーの自己申告エンドポイント（/internal/introspection）が
// メッシュ内部限定で到達でき、担当段（ingest）を申告することを検証する。これにより ingest 段が
// ドリフト検出で Unverifiable でなくなる。
[Trait("TestKind", "Integration")]
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

    // FR-15, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 4, IADR-0462 (#1514, #1255 経路 ⑤):
    // 本番の Program.cs が**自己申告の gRPC 面**を `ServiceCaller` 付きで張っている。
    // 経路は宛先の側が面を持たないと 1 つも移らない（扇形）ので、宛先ごとに固定する。
    // 呼び出しは TestServer 経由（待ち受けない）。s2s を持たない要求は認可で止まり、
    // UNIMPLEMENTED（面が無い）でも INTERNAL（認可ミドルウェアが無い）でもないことを見る。
    [Fact]
    public async Task Maps_the_introspection_grpc_face_behind_ServiceCaller()
    {
        var server = _factory.Server;
        var endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/platform.introspection.v1.ServiceIntrospection/Get");
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)
            .Should().Contain(PlatformAuthPolicies.ServiceCaller);

        using var channel = GrpcChannel.ForAddress(server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
        var client = new Pb.ServiceIntrospection.ServiceIntrospectionClient(channel);
        var act = async () => await client.GetAsync(
            new Pb.GetServiceIntrospectionRequest(), cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().BeOneOf(StatusCode.Unauthenticated, StatusCode.PermissionDenied);
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
