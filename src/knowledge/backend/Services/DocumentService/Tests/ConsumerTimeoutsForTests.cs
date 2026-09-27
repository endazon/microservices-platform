using DocumentService.Features.Documents.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Messaging;

namespace DocumentService.Tests;

// FR-12 (#1657): カタログ登録の受け口を組み立てる試験のための、本番と同じ登録から引いた時間切れの判定。
internal static class ConsumerTimeoutsForTests
{
    public static ConsumerCallTimeouts Calls() =>
        new ServiceCollection()
            .AddLogging()
            .AddPlatformConsumerTimeouts()
            .BuildServiceProvider()
            .GetRequiredService<ConsumerCallTimeouts>();

    // 期限の試験以外は既定の上限を使う（どの呼び出しも即時に返すので、期限は結果に効かない）。
    public static CatalogTimeouts Timeouts => CatalogTimeouts.Default;

    // MassTransit のハーネスへ受け口を登録する試験のための、本番の Program.cs と同じ登録。
    public static IServiceCollection AddCatalogTimeoutsForTests(this IServiceCollection services) =>
        services.AddSingleton(CatalogTimeouts.Default).AddPlatformConsumerTimeouts();
}
