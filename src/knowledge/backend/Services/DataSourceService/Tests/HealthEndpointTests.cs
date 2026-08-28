using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DataSourceService.Tests;

public class HealthEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetHealthLive_Returns200()
    {
        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    // #269: readiness は RabbitMQ.Client 7 と非互換の外部 health check（AspNetCore.HealthChecks.Rabbitmq、
    // TypeLoadException 'IModel'）を使わない。ブローカ疎通は W4 の AddPlatformWolverineBroker() が満たす（#441 E1 で MassTransit を撤去した）。
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
        var response = await factory.CreateClient().GetAsync("/datasources", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
