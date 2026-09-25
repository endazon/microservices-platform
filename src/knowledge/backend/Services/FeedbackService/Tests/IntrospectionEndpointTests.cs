using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.Introspection.V1;

namespace FeedbackService.Tests;

// FR-15, IADR-0029 (#143): 自己申告エンドポイントが到達でき、サービス名を申告することを検証する
// （段・合成可能ポートは持たない存在申告のみのサービス）。
[Trait("TestKind", "Integration")]
public class IntrospectionEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public IntrospectionEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Reports_service_presence()
    {
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/internal/introspection", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await res.Content.ReadFromJsonAsync<ServiceIntrospectionDto>(TestContext.Current.CancellationToken);
        report.Should().NotBeNull();
        report!.Service.Should().Be("feedback-service");
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
}
