using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Api.Tests;

// FR-15, IADR-0029 (#143): 自己申告エンドポイントが到達でき、LLM 生成・埋め込みの合成可能ポート
// （llm / embedding ルータ）を申告することを検証する。
public class IntrospectionEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public IntrospectionEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Reports_service_and_router_ports()
    {
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/internal/introspection", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await res.Content.ReadFromJsonAsync<ServiceIntrospectionDto>(TestContext.Current.CancellationToken);
        report.Should().NotBeNull();
        report!.Service.Should().Be("llm-gateway");
        report.Ports.Select(p => p.Port).Should().Contain(["llm", "embedding"]);
    }
}
