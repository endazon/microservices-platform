using AwesomeAssertions;
using System.Net;
using System.Net.Http.Json;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DocumentService.Api.Tests;

public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // FR-06: ヘルスエンドポイント
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // #269: readiness は RabbitMQ.Client 7 と非互換の外部 health check（AspNetCore.HealthChecks.Rabbitmq、
    // TypeLoadException 'IModel'）を使わない。ブローカ疎通は MassTransit 組み込みの bus health check で満たす。
    [Fact]
    public void Readiness_DoesNotRegisterIncompatibleRabbitMqHealthCheck()
    {
        var options = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>();
        var names = options.Value.Registrations.Select(r => r.Name);
        names.Should().NotContain("rabbitmq");
    }

    // FR-06, UC-03: 文書一覧エンドポイント
    [Fact]
    public async Task GetDocuments_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/documents");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var docs = await response.Content.ReadFromJsonAsync<List<DocumentDto>>();
        docs.Should().NotBeNull();
    }

    // FR-06, UC-03: 文書登録エンドポイント
    [Fact]
    public async Task PostDocument_Returns201()
    {
        var req = new { Title = "テスト文書", OriginalUri = (string?)null, ContentType = (string?)null, Attributes = new Dictionary<string, string> { ["confidentiality"] = "internal" }, Tags = new List<string>() };
        var response = await factory.CreateClient().PostAsJsonAsync("/documents", req);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
