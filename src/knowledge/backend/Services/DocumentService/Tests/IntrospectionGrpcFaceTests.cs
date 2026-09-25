using System.Net.Http.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.Introspection.V1;

namespace DocumentService.Tests;

// FR-15, NFR-16, IADR-0029, IADR-0462 (#1514): 自己申告の面（REST と gRPC）を本番の Program.cs が張る（document-service）。
[Trait("TestKind", "Integration")]
public class IntrospectionGrpcFaceTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 対照: REST の面も同じ申告（サービス名）を返す（gRPC だけを張って REST を落としていない）。
    [Fact]
    public async Task Reports_service_presence_over_rest()
    {
        var report = await factory.CreateClient().GetFromJsonAsync<Platform.Shared.Contracts.Dtos.ServiceIntrospectionDto>(
            "/internal/introspection", TestContext.Current.CancellationToken);
        report.Should().NotBeNull();
        report!.Service.Should().Be("document-service");
    }

    // FR-15, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 4, IADR-0462 (#1514, #1255 経路 ⑤):
    // 本番の Program.cs が**自己申告の gRPC 面**を `ServiceCaller` 付きで張っている。
    // 経路は宛先の側が面を持たないと 1 つも移らない（扇形）ので、宛先ごとに固定する。
    // 呼び出しは TestServer 経由（待ち受けない）。s2s を持たない要求は認可で止まり、
    // UNIMPLEMENTED（面が無い）でも INTERNAL（認可ミドルウェアが無い）でもないことを見る。
    [Fact]
    public async Task Maps_the_introspection_grpc_face_behind_ServiceCaller()
    {
        var server = factory.Server;
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
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
