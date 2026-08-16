using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

public static class ObservabilityExtensions
{
    private const string ServiceVersion = "0.1.0";
    private const string DefaultOtlpEndpoint = "http://otel-collector:4317";

    // ADR-0006, IADR-0216: ログ・トレース・メトリクスは同じ OTLP 先と同じリソース属性を共有する。
    // 「相関 ID で三者を突合する」（ADR-0006 §決定）はリソース属性が一致していて初めて成立するため、
    // 導出を 2 箇所に書かず、この 2 つのヘルパへ集約する。
    private static Uri OtlpEndpointOf(IConfiguration config) =>
        new(config["Otlp:Endpoint"] ?? DefaultOtlpEndpoint);

    private static ResourceBuilder ResourceOf(string serviceName) =>
        ResourceBuilder.CreateDefault().AddService(serviceName, serviceVersion: ServiceVersion);

    // ADR-0006: OTel/Prometheus/Loki/Tempo への統一計装
    public static IServiceCollection AddPlatformObservability(
        this IServiceCollection services,
        IConfiguration config,
        string serviceName)
    {
        var otlpEndpoint = OtlpEndpointOf(config);
        var resourceBuilder = ResourceOf(serviceName);

        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o => o.Endpoint = otlpEndpoint))
            .WithMetrics(metrics => metrics
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(o => o.Endpoint = otlpEndpoint));

        return services;
    }

    // ADR-0030, IADR-0216: ロギングは標準 ILogger + OpenTelemetry Logs に一本化する（Serilog は不採用）。
    // 旧 ConfigurePlatformSerilog の置換。UseSerilog が全プロバイダを置換していたのと異なり、これは
    // 既定の Console プロバイダへ「追加」する形であり、コンソール出力は標準の書式で残る。
    public static ILoggingBuilder AddPlatformLogging(
        this ILoggingBuilder logging,
        IConfiguration config,
        string serviceName)
    {
        var otlpEndpoint = OtlpEndpointOf(config);
        var resourceBuilder = ResourceOf(serviceName);

        logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resourceBuilder);
            // ADR-0006: 相関 ID の突合。CorrelationIdMiddleware の BeginScope を LogRecord の属性へ載せる。
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
            // 構造化ログの項目（AuditLogger の {AuditAction} 等）を LogRecord の属性へ展開する。
            options.ParseStateValues = true;
            options.AddOtlpExporter(o => o.Endpoint = otlpEndpoint);
        });

        return logging;
    }
}
