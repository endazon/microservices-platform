using IngestionService.Features.Ingestion.Ingest;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Messaging;

namespace IngestionService.Tests;

// FR-02 (#1640): 取り込みの受け口を直接組み立てる試験のための、本番と同じ登録から引いた時間切れの判定。
internal static class ConsumerTimeoutsForTests
{
    public static ConsumerCallTimeouts Calls() =>
        new ServiceCollection()
            .AddLogging()
            .AddPlatformConsumerTimeouts()
            .BuildServiceProvider()
            .GetRequiredService<ConsumerCallTimeouts>();

    // 期限の試験以外は既定の上限を使う（どの呼び出しも即時に返すので、期限は結果に効かない）。
    public static IngestionTimeouts Timeouts => IngestionTimeouts.Default;
}
