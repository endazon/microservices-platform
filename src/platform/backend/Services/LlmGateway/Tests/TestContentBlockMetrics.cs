using System.Diagnostics.Metrics;
using LlmGateway.Common.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace LlmGateway.Tests;

// IADR-0535 (#1917), IADR-0562 (#1749): ClaudeProvider が要求する計器（本文に使わず捨てた content ブロックの件数）を、試験ごとに独立した容器から作る。
// IMeterFactory は容器ごとに別の Meter を作るので、計器を見ない試験の発行が他の probe へ混ざらない（[[IADR-0394]] 決定 1）。
internal static class TestContentBlockMetrics
{
    public static AnthropicContentBlockMetrics Create() => Create(out _);

    public static AnthropicContentBlockMetrics Create(out IMeterFactory meterFactory)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        meterFactory = services.BuildServiceProvider().GetRequiredService<IMeterFactory>();
        return new AnthropicContentBlockMetrics(meterFactory);
    }
}
