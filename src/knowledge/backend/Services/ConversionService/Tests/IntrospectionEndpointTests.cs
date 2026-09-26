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
using Platform.Shared.Infrastructure.Foundation.Introspection;
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
    // ［2026-09-26 / #1520］NFR-09, ADR-0109 決定 3, IADR-0465: 本サービスが認証を持ったので、面は
    // 他サービスと同じ形で判定する（従前は認可の登録が無く、どの要求も INTERNAL で落ちる fail-closed だった）。
    // s2s 無し → UNAUTHENTICATED、利用者のトークン（管理者であっても）→ PERMISSION_DENIED、
    // `platform-service` のトークン → 申告が返る。**本物の JwtBearer**（検証鍵だけテスト用）を通す。
    // ［2026-09-26 / #1537］IADR-0462 フォローアップ 3: 配備の gRPC 宛先（helm・compose）・h2c リスナを配線した
    // （配線そのものは `IntrospectionGrpcDeploymentWiringTests` が固定する）。構成情報 API の収集器は
    // この面の応答を `IntrospectionGrpcMapping.ToDto` で DTO へ戻し、空の `service` を到達不能へ落とす ——
    // 同じ写しで戻した申告が REST の申告と一致し、`service` を持つことを見る（輸送を変えても突合の入力が変わらない）。
    // 呼び出しは TestServer 経由（待ち受けない）。
    [Fact]
    public async Task Introspection_grpc_face_is_mapped_behind_ServiceCaller_and_judges_the_caller()
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
        var ct = TestContext.Current.CancellationToken;
        static Metadata Bearer(string token) => new() { { "authorization", "Bearer " + token } };

        var anonymous = async () => await client.GetAsync(new Pb.GetServiceIntrospectionRequest(), cancellationToken: ct);
        (await anonymous.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);

        var asUser = async () => await client.GetAsync(new Pb.GetServiceIntrospectionRequest(),
            Bearer(TestUserTokens.Issue("alice", ["platform-admin"])), cancellationToken: ct);
        (await asUser.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);

        var asService = await client.GetAsync(new Pb.GetServiceIntrospectionRequest(),
            Bearer(TestUserTokens.Issue("service-account-bff", ["platform-service"])), cancellationToken: ct);
        asService.Should().NotBeNull();

        // 対照: REST の面は従来どおり資格情報なしで申告を返す（門を持たない口）。
        var rest = await _factory.CreateClient().GetAsync("/internal/introspection", ct);
        rest.StatusCode.Should().Be(HttpStatusCode.OK);

        // FR-15, IADR-0462 決定 4 (#1537): 収集器と同じ写しで戻した gRPC の申告は REST の申告と同じである。
        var restReport = await rest.Content.ReadFromJsonAsync<ServiceIntrospectionDto>(ct);
        var grpcReport = IntrospectionGrpcMapping.ToDto(asService);
        grpcReport.Service.Should().Be("conversion-service", "空の service は収集器が到達不能へ落とす");
        grpcReport.Steps.Should().ContainSingle(s => s.Name == "convert", "対照: 申告が空のまま一致しているのではない");
        grpcReport.Should().BeEquivalentTo(restReport!, o => o.WithStrictOrdering());
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

                // NFR-09, ADR-0109 決定 3, IADR-0465 (#1520): 門は本物の JwtBearer で判定する（検証鍵だけ差し替え）。
                TestUserTokens.UseStaticJwtBearer(services);
            });
        }
    }
}
