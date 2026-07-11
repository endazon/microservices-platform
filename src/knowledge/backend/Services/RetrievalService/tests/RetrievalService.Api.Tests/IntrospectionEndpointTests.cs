using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Platform.Shared.Contracts.Dtos;

namespace RetrievalService.Api.Tests;

// FR-15, IADR-0029 (#143): 自己申告エンドポイントが到達でき、選択中の合成可能ポート
// （vector-store / embedding）を申告することを検証する。
public class IntrospectionEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public IntrospectionEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Reports_service_and_selected_ports()
    {
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/internal/introspection");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await res.Content.ReadFromJsonAsync<ServiceIntrospectionDto>();
        report.Should().NotBeNull();
        report!.Service.Should().Be("retrieval-service");
        report.Ports.Select(p => p.Port).Should().Contain(["vector-store", "embedding"]);
    }
}
