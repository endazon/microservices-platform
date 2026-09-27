using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Wolverine;
using Wolverine.Runtime.Handlers;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Messaging;

// FR-02, FR-13, FR-17, ADR-0027 (#1640): 受け口の外への呼び出しの期限（`ConsumerCallTimeouts`）と、
// 受け口の実行期限の組み立て（`ConsumerHandlerTimeouts` / `HandlerExecutionTimeoutPolicy<T>`）。
//
// 🔴 判定の境界は「自分の期限が立ち、呼び出し元の ct は立っていない」である（#1604 / #1621 と同じ絞り）。
// 対照として、呼び出し元の取り消しは畳まずに外へ出ることを毎回並べて測る。
[Trait("TestKind", "Unit")]
public sealed class ConsumerCallTimeoutsTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection()
        .AddLogging()
        .AddPlatformConsumerTimeouts()
        .BuildServiceProvider();

    private readonly MeterListener _listener = new();
    private readonly List<(long Value, Dictionary<string, object?> Tags)> _recorded = [];

    public ConsumerCallTimeoutsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ConsumerTimeoutMetrics.MeterName
                && instrument.Name == ConsumerTimeoutMetrics.TimeoutCounterName
                && ReferenceEquals(instrument.Meter.Scope, _services.GetRequiredService<IMeterFactory>()))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            lock (_recorded)
                _recorded.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _services.Dispose();
    }

    private ConsumerCallTimeouts Calls => _services.GetRequiredService<ConsumerCallTimeouts>();

    private static CancellationTokenSource CallerWithDeadline()
    {
        var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromSeconds(10));
        return caller;
    }

    private static async Task<string> HangAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return "unreachable";
    }

    [Fact]
    public async Task 自分の期限が立てば時間切れとして投げ直し計器に残す()
    {
        // 呼び出し元の ct は 10 秒で立つ（受け口の実行期限の縮尺）。期限を外す変異では、これが立って取り消しとして落ちる。
        using var caller = CallerWithDeadline();

        var act = () => Calls.RunAsync("ingest", "embedding", TimeSpan.FromMilliseconds(200), HangAsync, caller.Token);

        var thrown = (await act.Should().ThrowAsync<ConsumerTimeoutException>()).Which;
        thrown.Should().BeAssignableTo<TimeoutException>();
        thrown.Step.Should().Be("ingest");
        thrown.Target.Should().Be("embedding");
        thrown.Limit.Should().Be(TimeSpan.FromMilliseconds(200));
        thrown.InnerException.Should().BeAssignableTo<OperationCanceledException>();
        _recorded.Should().ContainSingle().Which.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            [ConsumerTimeoutMetrics.StepTag] = "ingest",
            [ConsumerTimeoutMetrics.TargetTag] = "embedding",
        });
    }

    // 対照: 呼び出し元の ct（停止要求・受け口の実行期限）が立ったら、自分の期限より前でも後でも畳まずに外へ出す。
    [Fact]
    public async Task 呼び出し元の取り消しは時間切れにせず外へ出す()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(100));

        var act = () => Calls.RunAsync("ingest", "embedding", TimeSpan.FromSeconds(30), HangAsync, caller.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.IsCancellationRequested.Should().BeTrue();
        _recorded.Should().BeEmpty("取り消しは時間切れとして数えない");
    }

    // 境界: 呼び出し元の ct と自分の期限の**両方**が立っていたら、呼び出し元の取り消しを優先する（`!ct.IsCancellationRequested`）。
    // 呼び出しは取り消しに遅れて応じる（300 ms 後に投げる）ので、捕捉の時点では両方が立っている。
    [Fact]
    public async Task 両方が立っていれば呼び出し元の取り消しを優先する()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => Calls.RunAsync<string>("ingest", "embedding", TimeSpan.FromMilliseconds(100), async t =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
            t.IsCancellationRequested.Should().BeTrue();
            throw new OperationCanceledException(t);
        }, caller.Token);

        var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;
        thrown.Should().NotBeAssignableTo<TimeoutException>();
        _recorded.Should().BeEmpty();
    }

    // gRPC の生成クライアントは ct の取り消しを `RpcException(Cancelled)` で表す。自分の期限が立った後なら型を問わず時間切れ。
    [Fact]
    public async Task 期限の後に投げられた_RpcException_も時間切れとして扱う()
    {
        using var caller = CallerWithDeadline();
        var act = () => Calls.RunAsync<string>("retrieval-delete", "vector-store", TimeSpan.FromMilliseconds(100),
            async t =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, t);
                }
                catch (OperationCanceledException)
                {
                    throw new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client."));
                }
                return "unreachable";
            }, caller.Token);

        (await act.Should().ThrowAsync<ConsumerTimeoutException>())
            .Which.InnerException.Should().BeOfType<RpcException>();
        _recorded.Should().ContainSingle();
    }

    // 対照: 呼び出し元が gRPC の形で取り消されたときも、そのまま外へ出す（`RpcException(Cancelled)` を時間切れに化けさせない）。
    [Fact]
    public async Task 呼び出し元の取り消しによる_RpcException_はそのまま外へ出す()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(100));

        var act = () => Calls.RunAsync<string>("retrieval-delete", "vector-store", TimeSpan.FromSeconds(30),
            async t =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, t);
                }
                catch (OperationCanceledException)
                {
                    throw new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client."));
                }
                return "unreachable";
            }, caller.Token);

        await act.Should().ThrowAsync<RpcException>();
        _recorded.Should().BeEmpty();
    }

    // 期限より前の失敗（依存先のエラー）は何も変えずに外へ出す。成功はそのまま値を返す。
    [Fact]
    public async Task 期限より前の失敗と成功は変えない()
    {
        var failing = () => Calls.RunAsync<string>("wiki-sync", "wiki-js", TimeSpan.FromSeconds(30),
            _ => throw new HttpRequestException("503"), TestContext.Current.CancellationToken);
        await failing.Should().ThrowExactlyAsync<HttpRequestException>();

        var value = await Calls.RunAsync("wiki-sync", "wiki-js", TimeSpan.FromSeconds(30),
            _ => Task.FromResult(42), TestContext.Current.CancellationToken);
        value.Should().Be(42);

        var ran = false;
        await Calls.RunAsync("wiki-sync", "wiki-js", TimeSpan.FromSeconds(30),
            _ => { ran = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        ran.Should().BeTrue();
        _recorded.Should().BeEmpty();
    }

    // 呼び出しへ渡る ct は呼び出し元の ct と自分の期限の連結である（呼び出し元の ct そのものを渡すと期限が効かない）。
    [Fact]
    public async Task 呼び出しへ渡る_ct_は呼び出し元の_ct_ではない()
    {
        var caller = TestContext.Current.CancellationToken;
        CancellationToken seen = default;

        await Calls.RunAsync("ingest", "content", TimeSpan.FromSeconds(30),
            t => { seen = t; return Task.CompletedTask; }, caller);

        seen.Should().NotBe(caller);
        seen.CanBeCanceled.Should().BeTrue();
    }

    [Fact]
    public void 総枠の使い切りは同じ計器と例外型で表す()
    {
        var thrown = Calls.BudgetExhausted("ingest", "embedding-budget", TimeSpan.FromSeconds(600), "3 of 10 chunks");

        thrown.Should().BeOfType<ConsumerTimeoutException>().Which.Target.Should().Be("embedding-budget");
        thrown.Message.Should().Contain("3 of 10 chunks");
        _recorded.Should().ContainSingle().Which.Tags[ConsumerTimeoutMetrics.TargetTag].Should().Be("embedding-budget");
    }
}

[Trait("TestKind", "Unit")]
public class ConsumerHandlerTimeoutsTests
{
    // 前提の固定: 方針を入れない受け口の予算は Wolverine の既定に対して検査する。版更新で変わったら見直す。
    [Fact]
    public void Wolverine_の既定の実行期限と一致する()
    {
        ConsumerHandlerTimeouts.WolverineDefault.Should().Be(new WolverineOptions().DefaultExecutionTimeout);
        ConsumerHandlerTimeouts.WolverineDefault.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void 最悪の所要時間が実行期限に収まれば通る()
    {
        var act = () => ConsumerHandlerTimeouts.EnsureFits("wiki-sync", TimeSpan.FromSeconds(60), "Wolverine の既定",
            ("本文の取得", TimeSpan.FromSeconds(20)), ("Wiki.js", TimeSpan.FromSeconds(39)));

        act.Should().NotThrow();
    }

    // 境界: ちょうど等しいときも止める（受け口の ct と同着では、どちらが先に立つかが定まらない）。
    [Theory]
    [InlineData(40)]
    [InlineData(45)]
    public void 最悪の所要時間が実行期限以上なら起動を止める(int secondPart)
    {
        var act = () => ConsumerHandlerTimeouts.EnsureFits("wiki-sync", TimeSpan.FromSeconds(60), "Wolverine の既定",
            ("本文の取得", TimeSpan.FromSeconds(20)), ("Wiki.js", TimeSpan.FromSeconds(secondPart)));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*wiki-sync*Wolverine の既定*本文の取得*Wiki.js*");
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("7", 7)]
    public void 秒数は既定と下限一秒で読む(string? configured, int expectedSeconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["X:Seconds"] = configured })
            .Build();

        ConsumerHandlerTimeouts.Seconds(configuration, "X:Seconds", 30).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    // 方針は型引数のメッセージ型の受け口にだけ期限を与え、他の型の既定（未設定）は変えない。端数は切り上げる。
    [Fact]
    public void 実行期限の方針は指定した型の受け口にだけ効く()
    {
        var graph = new HandlerGraph();
        var target = new HandlerChain(typeof(ProbeMessage), graph);
        var other = new HandlerChain(typeof(OtherMessage), graph);

        new HandlerExecutionTimeoutPolicy<ProbeMessage>(TimeSpan.FromSeconds(719.2))
            .Apply([target, other], new JasperFx.CodeGeneration.GenerationRules(), null!);

        target.ExecutionTimeoutInSeconds.Should().Be(720);
        other.ExecutionTimeoutInSeconds.Should().BeNull();
    }

    private sealed record ProbeMessage;
    private sealed record OtherMessage;
}
