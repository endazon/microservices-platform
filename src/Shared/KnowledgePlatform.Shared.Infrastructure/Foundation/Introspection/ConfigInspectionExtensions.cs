using KnowledgePlatform.Shared.Infrastructure.Foundation.Audit;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KnowledgePlatform.Shared.Infrastructure.Foundation.Introspection;

// FR-15: 構成情報 API（集約・ドリフト検出・監査）の依存を一括登録する。
// 実装配置は独立サービス化せず既存の合成ホスト（BFF）へ同居させる（過剰分割回避。IADR に記録）。
public static class ConfigInspectionExtensions
{
    public static IHostApplicationBuilder AddKnowledgePlatformConfigInspection(
        this IHostApplicationBuilder builder)
    {
        // 宣言（pipeline.json）を読み込み、突合の基準として共有する。
        builder.AddKnowledgePlatformPipelineConfig();
        var pipeline = builder.Configuration.GetKnowledgePlatformPipeline();
        builder.Services.AddSingleton(pipeline);

        builder.Services.Configure<IntrospectionOptions>(
            builder.Configuration.GetSection(IntrospectionOptions.SectionName));
        builder.Services.Configure<ConfigVersionOptions>(
            builder.Configuration.GetSection(ConfigVersionOptions.SectionName));
        builder.Services.Configure<DriftDetectionOptions>(
            builder.Configuration.GetSection(DriftDetectionOptions.SectionName));

        builder.Services.AddHttpClient(HttpEffectiveConfigCollector.HttpClientName);
        builder.Services.TryAddSingletonTimeProvider();

        builder.Services.AddSingleton<IEffectiveConfigCollector, HttpEffectiveConfigCollector>();
        builder.Services.AddSingleton<IConfigInspectionService, ConfigInspectionService>();
        builder.Services.AddSingleton<IDriftAlertSink, LoggingDriftAlertSink>();
        builder.Services.AddSingleton<IAuditLogger, AuditLogger>();

        // 定期ドリフト検出（既定 5 分）。Drift:Enabled=false で無効化できる。
        builder.Services.AddHostedService<DriftDetectionHostedService>();

        return builder;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
            services.AddSingleton(TimeProvider.System);
    }
}
