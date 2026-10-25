using System.Diagnostics.Metrics;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Messaging;

// NFR-21, ADR-0053 決定 1〜3, ADR-0012, IADR-0592 (#2043): **自動再試行を使い切ったメッセージを数える計器。**
//
// 🔴 観測点は Wolverine が実行時に引く判定関数（`FailureRuleCollection.DetermineExecutionContinuation`）と、
// それが返す継続の実行である。「規則を登録した」だけを見ると、計器を上げる継続が実行されなくても緑になる。
// 実行の試験は本物の Wolverine ホストの runtime と、ホストの IMeterFactory に絞った MeterListener で測る
// （再試行の待ち 2/10/30 秒を実時間で回すと 42 秒かかるため、上限到達の試行の継続を直接実行する）。
[Trait("TestKind", "Unit")]
public sealed class DeadLetterMetricsTests
{
    private sealed record ProbeMessage(Guid Id);

    private static MethodInfo DetermineContinuationMethod =>
        typeof(FailureRuleCollection).GetMethod(
            "DetermineExecutionContinuation",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "FailureRuleCollection.DetermineExecutionContinuation が見つかりません。"
            + " Wolverine の版更新で再試行判定の観測点が変わった可能性があります（ADR-0053 決定 2）。");

    private static IContinuation ContinuationAt(WolverineOptions options, Exception exception, Envelope envelope)
        => (IContinuation)(DetermineContinuationMethod.Invoke(options.Policies.Failures, [exception, envelope])
           ?? throw new InvalidOperationException($"試行 {envelope.Attempts} 回目の継続が null でした。"));

    // 継続を平たくする（`.And(...)` を付けると Wolverine は CompositeContinuation で束ねる）。
    // CompositeContinuation と MoveToErrorQueue は internal なので、型名と `Inner` をリフレクションで読む。
    // 名前が消えれば束ねた中身が見えず、下の assert が落ちる（版更新で静かに no-op 化しない）。
    internal static IContinuation[] Flatten(IContinuation continuation)
    {
        if (continuation.GetType().Name != "CompositeContinuation")
            return [continuation];
        var inner = continuation.GetType().GetProperty("Inner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(continuation) as IEnumerable<IContinuation>
            ?? throw new InvalidOperationException(
                "CompositeContinuation.Inner が見つかりません。Wolverine の版更新で継続の束ね方が変わった可能性があります（ADR-0053 決定 2）。");
        return [.. inner.SelectMany(Flatten)];
    }

    private static bool IsMoveToErrorQueue(IContinuation c) => c.GetType().Name == "MoveToErrorQueue";

    // `.And(action, ...)` は internal の LambdaContinuation になり、説明文は残らない。束ねた委譲先が
    // `WolverineExtensions.RecordDeadLetterAsync` であることで計器の継続と見分ける。
    internal static bool IsDeadLetterCounter(IContinuation c) =>
        c.GetType().Name == "LambdaContinuation"
        && c.GetType().GetField("_action", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(c) is Delegate action
        && action.Method.Name == nameof(WolverineExtensions.RecordDeadLetterAsync)
        && action.Method.DeclaringType == typeof(WolverineExtensions);

    private static WolverineOptions Defaults()
    {
        var options = new WolverineOptions();
        options.UsePlatformMessagingDefaults();
        return options;
    }

    [Fact]
    public void 前提_共通既定を当てる前は上限到達の試行に計器が無い()
    {
        // 変異が当たることの前提条件。Wolverine の既定がもともと数えているなら、以降の試験は何も証明しない。
        var continuation = ContinuationAt(new WolverineOptions(), new InvalidOperationException("x"),
            new Envelope { Attempts = WolverineExtensions.MaxAttempts });

        Flatten(continuation).Should().NotContain(c => IsDeadLetterCounter(c));
    }

    [Fact]
    public void 上限に達した試行はデッドレターへの移送と計器の両方を行う()
    {
        var continuation = ContinuationAt(Defaults(), new InvalidOperationException("x"),
            new Envelope { Attempts = WolverineExtensions.MaxAttempts });

        var parts = Flatten(continuation);
        parts.Should().ContainSingle(c => IsMoveToErrorQueue(c), "移送はそのまま行う（計器は回復の経路を置き換えない）");
        parts.Should().ContainSingle(c => IsDeadLetterCounter(c), "ADR-0053 決定 2: 再試行を使い切った時点で数える");
    }

    [Fact]
    public void 上限の手前の試行では計器を上げない()
    {
        // 再試行で回復し得る失敗を数えると、平常の一時障害で警報が鳴る（0 が正常の計器ではなくなる）。
        var options = Defaults();
        for (var attempt = 1; attempt < WolverineExtensions.MaxAttempts; attempt++)
        {
            Flatten(ContinuationAt(options, new InvalidOperationException("x"), new Envelope { Attempts = attempt }))
                .Should().NotContain(c => IsDeadLetterCounter(c) || IsMoveToErrorQueue(c), $"試行 {attempt} 回目は再試行である");
        }
    }

    [Fact]
    public void 残余_上限を超えた試行番号で届いた失敗はWolverineの既定の移送で計器を通らない()
    {
        // IADR-0592 §残余: 規則の段は試行番号ごとに置かれ、上限（4）を超えた番号には段が無い。Wolverine はそこで
        // 既定の移送（計器なし）へ倒す。1 回の配信の中の再試行は 4 で必ず止まるので通常は生じない —— 生じるのは
        // 試行回数を持ったまま届き直した場合（DLQ の写しの再投入。運用では再投入しない）だけである。
        // **この残余を黙って広げない・黙って閉じない**ために挙動を固定する（Wolverine の版更新で変われば赤になる）。
        var parts = Flatten(ContinuationAt(Defaults(), new InvalidOperationException("x"),
            new Envelope { Attempts = WolverineExtensions.MaxAttempts + 1 }));

        parts.Should().ContainSingle(c => IsMoveToErrorQueue(c), "上限を超えても再試行へ戻らない");
        parts.Should().NotContain(c => IsDeadLetterCounter(c));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(HttpRequestException))]
    public void 例外の種類によらず上限到達で数える(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Flatten(ContinuationAt(Defaults(), exception, new Envelope { Attempts = WolverineExtensions.MaxAttempts }))
            .Should().Contain(c => IsDeadLetterCounter(c));
    }

    [Fact]
    public async Task 上限到達の継続を実行すると1回だけ数えメッセージ型と例外の型を載せる()
    {
        using var host = new HostBuilder()
            .UseWolverine(opts =>
            {
                // ハンドラを持たないホストなのでコード生成は起きない（実行時コンパイラを参照しない試験プロジェクトでも起動できる）。
                opts.Discovery.DisableConventionalDiscovery();
                opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Static;
                opts.UsePlatformMessagingDefaults();
            })
            .ConfigureServices(services => services.DisableAllExternalWolverineTransports())
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        var recorded = new List<(long Value, Dictionary<string, object?> Tags)>();
        var meterFactory = host.Services.GetRequiredService<IMeterFactory>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == DeadLetterMetrics.MeterName
                && instrument.Name == DeadLetterMetrics.DeadLetterCounterName
                && ReferenceEquals(instrument.Meter.Scope, meterFactory))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            lock (recorded)
                recorded.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        });
        listener.Start();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var envelope = new Envelope(new ProbeMessage(Guid.NewGuid())) { Attempts = WolverineExtensions.MaxAttempts };
        var exception = new TimeoutException("依存先が応答しない");
        var lifecycle = RecordingLifecycle.Create(envelope);

        var continuation = ContinuationAt(runtime.Options, exception, envelope);
        await continuation.ExecuteAsync(lifecycle, runtime, DateTimeOffset.UtcNow, activity: null);
        await host.StopAsync(TestContext.Current.CancellationToken);

        ((RecordingLifecycle)(object)lifecycle).Calls.Should().Contain(nameof(IEnvelopeLifecycle.MoveToDeadLetterQueueAsync),
            "計器を足しても移送は行われる");
        recorded.Should().ContainSingle().Which.Should().BeEquivalentTo((1L, new Dictionary<string, object?>
        {
            [DeadLetterMetrics.MessageTypeTag] = nameof(ProbeMessage),
            [DeadLetterMetrics.ErrorTypeTag] = nameof(TimeoutException),
        }));
    }

    [Fact]
    public void 共通既定はホストのDIへ計器を登録する()
    {
        // 計器の継続は runtime の DI から引く。登録が消えると継続は何もせずに終わる（例外で回復の経路を壊さないため）。
        using var host = new HostBuilder()
            .UseWolverine(opts =>
            {
                // ハンドラを持たないホストなのでコード生成は起きない（実行時コンパイラを参照しない試験プロジェクトでも起動できる）。
                opts.Discovery.DisableConventionalDiscovery();
                opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Static;
                opts.UsePlatformMessagingDefaults();
            })
            .ConfigureServices(services => services.DisableAllExternalWolverineTransports())
            .Build();

        host.Services.GetService<DeadLetterMetrics>().Should().NotBeNull();
    }

    [Theory]
    [InlineData(true, "ProbeMessage")]
    [InlineData(false, "probe.alias")]
    public void メッセージ型のタグは本体の型の短い名前を優先しエンベロープの型名で代える(bool withBody, string expected)
    {
        var envelope = withBody
            ? new Envelope(new ProbeMessage(Guid.NewGuid())) { MessageType = "probe.alias" }
            : new Envelope { MessageType = "probe.alias" };

        WolverineExtensions.MessageTypeOf(envelope).Should().Be(expected);
        WolverineExtensions.MessageTypeOf(null).Should().Be("unknown");
    }

    [Fact]
    public void 計器の名前を固定する()
    {
        // 警報（deploy/prometheus/alerts.yml ほか 3 か所の DeadLetterReached）は `messaging_dead_letter_total` を見る。
        // 名前を変えると式は構文として正当なまま永久に空ベクタになる（#1110 と同型）。scripts.repo.test.js が 4 か所を突き合わせる。
        DeadLetterMetrics.MeterName.Should().Be("microservices-platform.messaging");
        DeadLetterMetrics.DeadLetterCounterName.Should().Be("messaging.dead_letter");
        DeadLetterMetrics.MessageTypeTag.Should().Be("messaging.message.type");
        DeadLetterMetrics.ErrorTypeTag.Should().Be("error.type");
    }

    // IEnvelopeLifecycle の記録つきの代役。呼ばれたメソッド名を残し、戻り値は完了済みの既定値を返す。
    public class RecordingLifecycle : DispatchProxy
    {
        private Envelope _envelope = null!;
        public List<string> Calls { get; } = [];

        public static IEnvelopeLifecycle Create(Envelope envelope)
        {
            var proxy = Create<IEnvelopeLifecycle, RecordingLifecycle>();
            ((RecordingLifecycle)(object)proxy)._envelope = envelope;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == "get_Envelope")
                return _envelope;
            lock (Calls)
                Calls.Add(targetMethod.Name);
            var type = targetMethod.ReturnType;
            if (type == typeof(Task))
                return Task.CompletedTask;
            if (type == typeof(ValueTask))
                return ValueTask.CompletedTask;
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }
}
