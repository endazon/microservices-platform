using System.Diagnostics.Metrics;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Limits;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmGateway.Tests;

// NFR-18, IADR-0678 (#2404): 判定器（CompletionUseCase）を直接組む試験が使う上限の判定器。
// 既定（Report）なら送信の挙動を変えないので、上限が主題でない試験は既定のまま渡せばよい。
internal static class TestCallLimiters
{
    public static LlmCallLimiter Create(
        IMeterFactory meterFactory, LlmRoutingOptions routing, ModelPriceTable prices, TimeProvider time,
        LlmCallLimitOptions? options = null, ILlmSpendStore? store = null)
        => new(
            new TestClaudeProviders.StaticOptionsMonitor<LlmCallLimitOptions>(options ?? new LlmCallLimitOptions()),
            new TestClaudeProviders.StaticOptionsMonitor<LlmRoutingOptions>(routing),
            prices, time, new LlmCallLimitMetrics(meterFactory), new LogOccurrenceThrottle(time),
            NullLogger<LlmCallLimiter>.Instance, store ?? new ProcessLocalLlmSpendStore());

    // IADR-0701: 判定器（EmbedUseCase）を直接組む試験が使う、既定（Report）の上限の判定器。上限が主題でない試験は既定のまま渡せばよい。
    public static LlmCallLimiter Default(TimeProvider? time = null)
    {
        var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var prices = new ModelPriceTable(
            new TestClaudeProviders.StaticOptionsMonitor<ModelPricingOptions>(new ModelPricingOptions()),
            NullLogger<ModelPriceTable>.Instance);
        return Create(meters.GetRequiredService<IMeterFactory>(), new LlmRoutingOptions(), prices, time ?? TimeProvider.System);
    }
}
