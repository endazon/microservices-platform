using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Tests;

// FR-15, IADR-0029 (#143): 自己申告エンドポイントが到達でき、サービス名を申告することを検証する
// （段・合成可能ポートは持たない存在申告のみのサービス）。
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
        report!.Service.Should().Be("authorization-service");
    }
}
