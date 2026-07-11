using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

public static class HealthCheckExtensions
{
    public static IHealthChecksBuilder AddPlatformHealthChecks(
        this IServiceCollection services) =>
        services.AddHealthChecks();

    public static WebApplication MapPlatformHealthChecks(
        this WebApplication app)
    {
        // Liveness: プロセスが生きているか（依存不要）
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        });
        // Readiness: 依存サービスへの疎通確認
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = hc => hc.Tags.Contains("ready")
        });
        return app;
    }
}
