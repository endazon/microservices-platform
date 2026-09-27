using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Messaging;

namespace RetrievalService.Tests;

// FR-06 (#1640): 受け口を直接組み立てる試験のための、本番と同じ登録から引いた時間切れの判定。
internal static class ConsumerTimeoutsForTests
{
    public static ConsumerCallTimeouts Calls() =>
        new ServiceCollection()
            .AddLogging()
            .AddPlatformConsumerTimeouts()
            .BuildServiceProvider()
            .GetRequiredService<ConsumerCallTimeouts>();
}
