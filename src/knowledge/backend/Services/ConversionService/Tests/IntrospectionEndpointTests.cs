using System.Net;
using System.Net.Http.Json;
using ConversionService.Infrastructure.Persistence;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.Introspection.V1;

namespace ConversionService.Tests;

// FR-15, IADR-0029 (#142): conversion ワーカーの自己申告エンドポイント（/internal/introspection）が
// メッシュ内部限定で到達でき、担当段（convert）を申告することを検証する。これにより convert 段が
// ドリフト検出で Unverifiable でなくなる。
[Trait("TestKind", "Integration")]
public class IntrospectionEndpointTests : IClassFixture<IntrospectionEndpointTests.Factory>
{
    private readonly Factory _factory;

    public IntrospectionEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Introspection_endpoint_reports_convert_step()
    {
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/internal/introspection", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await res.Content.ReadFromJsonAsync<ServiceIntrospectionDto>(
            TestContext.Current.CancellationToken);
        report.Should().NotBeNull();
        report!.Service.Should().Be("conversion-service");
        report.Steps.Should().ContainSingle(s => s.Name == "convert")
            .Which.Enabled.Should().BeTrue();
    }

    // FR-15, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 4, IADR-0462 (#1514, #1255 経路 ⑤):
    // 自己申告の gRPC 面は共通基盤が REST と対で張るので、本サービスにも `ServiceCaller` 付きで張られている。
    // 🔴 **ただし本サービスは認証を持たない**（中継された利用者の資格情報を自ら検証する実装は
    // planning#651 の裁定による別作業であり、本スライスでは触らない）。したがって面は
    // **fail-closed**（どの要求も成功しない）であり、構成情報 API はこの宛先を REST のまま収集する
    // （helm・compose に gRPC の宛先を入れていない。`IntrospectionGrpcDeploymentWiringTests` の保留一覧）。
    // 認証が着地したらこの試験は他サービスと同じ形（s2s 無し → UNAUTHENTICATED）へ書き換える。
    // 呼び出しは TestServer 経由（待ち受けない）。
    [Fact]
    public async Task Introspection_grpc_face_is_mapped_behind_ServiceCaller_and_fails_closed_without_auth()
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

        // 認可の登録も認可ミドルウェアも無いので、要求は受け口の手前（EndpointMiddleware）で
        // 「認可メタデータを持つのに認可ミドルウェアが無い」例外になる。TestServer はアプリの例外を呼び出し側へ運び、
        // gRPC クライアントはそれを INTERNAL に包む（実配備では 500 → INTERNAL）。**匿名で申告が読めることは無い。**
        // 🔴 status と文言まで見る —— 例外型だけだと、輸送の失敗（接続できない等）でも緑になる。
        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.Internal);
        ex.Status.Detail.Should().Contain("authorization metadata");

        // 対照: REST の面は従来どおり申告を返す（gRPC 面を張ったことで REST を壊していない）。
        var rest = await _factory.CreateClient().GetAsync("/internal/introspection", TestContext.Current.CancellationToken);
        rest.StatusCode.Should().Be(HttpStatusCode.OK);
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
                // IADR-0043: 実 Postgres 接続（起動時 MigrateAsync）を避けるため DbContext を InMemory へ差し替える
                // （InMemory は非リレーショナルのため MigrateAsync はスキップされる）。
                services.ReplaceDbContextWithInMemory<ConversionJobDbContext>("IntrospectionTest");

                // 実 RabbitMQ 接続を避けるため MassTransit をテストハーネスへ差し替える。
                services.RemoveAll<IBusControl>();
                services.AddMassTransitTestHarness();
                // 🔴 ADR-0027（#441 E1）: Program.cs は Wolverine ホストも起こす。外部トランスポートを
                // 落とさないと、テストごとに実 RabbitMQ への接続再試行（実測 20 回・約 135 秒）が走り、
                // **落ちるのではなく黙って遅くなる**（1 テスト 2 分半）。ビルドも赤にならないので気づきにくい。
                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}
