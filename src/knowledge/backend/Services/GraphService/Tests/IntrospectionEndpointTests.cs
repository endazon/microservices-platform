using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests;

// FR-15, IADR-0029: 自己申告エンドポイントが到達でき、サービス名を申告する。
[Trait("TestKind", "Integration")]
public class IntrospectionEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task Reports_service_presence()
    {
        var res = await factory.CreateClient().GetAsync("/internal/introspection", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await res.Content.ReadFromJsonAsync<ServiceIntrospectionDto>(TestContext.Current.CancellationToken);
        report.Should().NotBeNull();
        report!.Service.Should().Be("graph-service");
    }
}
