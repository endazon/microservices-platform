using System.Diagnostics.Metrics;
using AwesomeAssertions;
using JasperFx.Blocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Messaging;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Messaging;

// FR-12, NFR-21, IADR-0609 決定 2, [[IADR-0656]] (#2178): **Wolverine の耐久化の書き込みのメモリ上の再試行の失敗を数える計器。**
//
// 🔴 観測点は **実物の `JasperFx.Blocks.RetryBlock<T>`** が出すログである（送出表への書き込み `StoreOutgoingAsync` が一時的に
//   失敗すると、Wolverine はこのブロックでメモリ上に再試行する。IADR-0609 の残余の窓）。テンプレートを試験の側に書き写して
//   照合すると、JasperFx の版上げで文面が変わっても緑のまま黙る。だから**本物のブロックを動かして**計器が進むことを測る
//   （版上げでテンプレートが変われば、ここが赤になる）。
// 照合はメッセージテンプレート（`{OriginalFormat}`）の完全一致 ＋ 例外つき ＋ Error 以上で、文面の部分一致はしない。
[Trait("TestKind", "Unit")]
public sealed class RetryBlockErrorMetricsTests
{
    private sealed record Recorded(long Value, Dictionary<string, object?> Tags);

    // 本番と同じ登録（`AddPlatformLogging`）でロガーを作り、同じ IMeterFactory に絞った MeterListener で計器を測る。
    private static (ServiceProvider Services, List<Recorded> Recorded, MeterListener Listener) Build()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddPlatformRetryBlockErrorMetrics();
        });
        var provider = services.BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();

        var recorded = new List<Recorded>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == RetryBlockErrorMetrics.MeterName
                && instrument.Name == RetryBlockErrorMetrics.CounterName
                && ReferenceEquals(instrument.Meter.Scope, meterFactory))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            lock (recorded)
                recorded.Add(new Recorded(value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        });
        listener.Start();
        return (provider, recorded, listener);
    }

    // 陽性: 実物の RetryBlock が 1 度失敗して再試行すると、計器が 1 進む（ラベルは例外の型だけ）。
    [Fact]
    public async Task 実物のRetryBlockの再試行の失敗で計器が1進む()
    {
        var (services, recorded, listener) = Build();
        using var _ = services;
        using var __ = listener;
        // 本番では Wolverine が渡すロガーである。カテゴリでは絞らないので、任意の名前で作ってよい。
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Wolverine.Probe");

        var calls = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var block = new RetryBlock<string>((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new TimeoutException("送出表への書き込みが一時的に失敗した（模擬）");
            completed.TrySetResult();
            return Task.CompletedTask;
        }, logger, TestContext.Current.CancellationToken);

        await block.PostAsync("envelope-batch");
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        lock (recorded)
            recorded.Should().ContainSingle().Which.Should().BeEquivalentTo(new Recorded(1, new Dictionary<string, object?>
            {
                [RetryBlockErrorMetrics.ErrorTypeTag] = nameof(TimeoutException),
            }), "再試行の失敗 1 回につき 1。ラベルは例外の型だけで、中身（item）は載せない");
    }

    // 陰性: 同じテンプレートでも例外なし・Error 未満は数えない。別のテンプレートの Error も数えない（陽性対照は上）。
    [Fact]
    public void テンプレートが違うか例外が無いかError未満のログは数えない()
    {
        var (services, recorded, listener) = Build();
        using var _ = services;
        using var __ = listener;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Wolverine.Probe");

#pragma warning disable CA2254 // テンプレートを定数として渡すのは、照合の境界を試すためである。
        logger.LogError(new TimeoutException(), "Error while retrying {Item}", "x");          // 別のテンプレート（文面が似ていても数えない）
        logger.LogError(RetryBlockErrorMetrics.RetryTemplate, "x");                           // 例外が無い
        logger.LogWarning(new TimeoutException(), RetryBlockErrorMetrics.RetryTemplate, "x"); // Error 未満
        logger.LogError(new TimeoutException(), "Completed {Item}", "x");                     // 同じブロックの別のログ
#pragma warning restore CA2254

        lock (recorded)
            recorded.Should().BeEmpty();
    }

    // 本番の登録（`AddPlatformLogging`）に提供者が入っていること。入っていなければ計器は永久に 0 本のまま黙る。
    [Fact]
    public void AddPlatformLoggingは提供者を登録する()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddPlatformLogging(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "probe"));

        services.Should().Contain(d => d.ServiceType == typeof(ILoggerProvider)
            && d.ImplementationType == typeof(RetryBlockErrorLoggerProvider));
    }
}
