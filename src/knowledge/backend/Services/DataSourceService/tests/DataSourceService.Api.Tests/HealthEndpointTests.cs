using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DataSourceService.Api.Tests;

public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
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

    [Fact]
    public async Task GetDataSources_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/datasources");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
