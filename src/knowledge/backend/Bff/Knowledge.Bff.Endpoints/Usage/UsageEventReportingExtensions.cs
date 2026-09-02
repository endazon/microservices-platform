using Microsoft.Extensions.DependencyInjection;

namespace Knowledge.Bff.Endpoints.Usage;

// FR-10, SC-10, [[IADR-0336]] (#1103): 利用状況イベントの発火側の配線。
//
// **BFF ホスト（Platform.Bff）から 1 行で呼ぶ。** 名前付きクライアント `DashboardService` は
// ホスト側に既に在る（`/bff/dashboard/summary` の集約が使うもの）ので、ここでは作らない。
//
// 🔴 **計器の Meter は BFF のサービス名と同じ**なので、OTLP の収集対象は増えない。
// ただし `AddMeter(UsageEventMetrics.MeterName)` の宣言はホスト側に要る（宣言が無い Meter は
// 収集されず、**送出の失敗が静かに消える**）。
public static class UsageEventReportingExtensions
{
    public static IServiceCollection AddKnowledgeUsageEventReporting(this IServiceCollection services)
    {
        services.AddMetrics();
        services.AddSingleton<UsageEventMetrics>();
        services.AddSingleton<UsageEventQueue>();
        services.AddSingleton<IUsageEventReporter, UsageEventReporter>();
        services.AddHostedService<UsageEventDispatcher>();
        return services;
    }
}
