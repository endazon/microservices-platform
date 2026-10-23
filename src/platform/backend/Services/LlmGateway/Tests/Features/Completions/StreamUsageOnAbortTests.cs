using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using LlmGateway.Features.Completions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Tests.Features.Completions;

// FR-10, ADR-0044 決定 3, IADR-0601 (#2116): 逐次応答が Done に届かずに終わっても、判明分のトークンを計上し、
// 不完全として数え、利用者の取り消しを `llm.result=cancelled` として数える。正常完了は二重に数えない。
//
// 判定器（CompletionUseCase）を直接組む（SentFalseObservabilityTests と同じ組み方）。計器は共有 Meter へ
// 発行するので SharedMeterCollection に入り、probe は**この試験の IMeterFactory が作った Meter インスタンス**だけを見る
// （IADR-0394）。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Unit")]
public sealed class StreamUsageOnAbortTests : IDisposable
{
    private const int Input = 120;

    private readonly ServiceProvider _meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero));
    private readonly Probe _probe;

    public StreamUsageOnAbortTests()
    {
        _probe = new Probe(_meterServices.GetRequiredService<IMeterFactory>().Create(LlmCompletionMetrics.MeterName));
    }

    public void Dispose()
    {
        _probe.Dispose();
        _meterServices.Dispose();
    }

    private sealed record Measured(string Instrument, double Value, Dictionary<string, string> Tags);

    private sealed class Probe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measured> _items = [];

        public Probe(Meter meter)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter))
                        l.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.SetMeasurementEventCallback<int>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.Start();
        }

        private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, string>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value?.ToString() ?? string.Empty;
            lock (_items) _items.Add(new Measured(name, value, dict));
        }

        public IReadOnlyList<Measured> Of(string instrument)
        {
            lock (_items) return [.. _items.Where(m => m.Instrument == instrument)];
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class Static<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FixedRouter(RoutingDecision decision) : ILlmRouter
    {
        public RoutingDecision Route(RoutingRequest request) => decision;
    }

    // 試験ごとに筋書きを渡すプロバイダ。ct はそのまま筋書きへ渡す（取り消しは筋書きの中の待ちで起きる）。
    private sealed class ScriptedProvider(
        Func<CancellationToken, IAsyncEnumerable<CompletionChunk>> script) : ILlmProvider
    {
        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<CompletionChunk> StreamAsync(CompletionRequest request, CancellationToken ct = default)
            => script(ct);
    }

    private CompletionUseCase UseCase(Func<CancellationToken, IAsyncEnumerable<CompletionChunk>> script)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ILlmProvider>("claude", new ScriptedProvider(script));
        var meterFactory = _meterServices.GetRequiredService<IMeterFactory>();
        var routing = new LlmRoutingOptions();
        routing.PurposeModels["rag-answer"] = "test-model";
        var pricing = new ModelPricingOptions();
        // 合成のモデル名と任意の単価（実価格ではない。LlmUsageMetricsTests と同じ値）。
        pricing.Models["test-model"] =
        [
            new ModelPriceEntry { InputPerMillionTokens = 3.0m, OutputPerMillionTokens = 15.0m },
        ];
        var prices = new ModelPriceTable(new Static<ModelPricingOptions>(pricing), NullLogger<ModelPriceTable>.Instance);
        return new CompletionUseCase(
            new FixedRouter(new RoutingDecision(true, "claude-managed", "claude", ProtectionTier.B, "test-model", false, "test")),
            services.BuildServiceProvider(),
            NullLoggerFactory.Instance,
            new LlmCompletionMetrics(meterFactory, new Static<LlmRoutingOptions>(routing)),
            new LlmUsageMetrics(meterFactory, new Static<LlmRoutingOptions>(routing), prices, _time),
            new LogOccurrenceThrottle(_time));
    }

    private static CompletionApiRequest Request()
        => new("本文", MaxTokens: 100, Confidentiality: "internal", Purpose: "rag-answer");

    private static CompletionChunk Progress(int input, int output = 0) => new(string.Empty, Done: false, input, output);

    // ---- 観測の読み出し ---------------------------------------------------------------------------

    private IReadOnlyList<string> Results()
        => [.. _probe.Of(LlmCompletionMetrics.CompletionCounterName).Select(m => m.Tags[LlmCompletionMetrics.ResultTag])];

    private double Tokens(string tokenType)
        => _probe.Of(LlmUsageMetrics.TokensCounterName)
            .Where(m => m.Tags[LlmUsageMetrics.TokenTypeTag] == tokenType).Sum(m => m.Value);

    private IReadOnlyList<Measured> Incomplete() => _probe.Of(LlmUsageMetrics.IncompleteCounterName);

    private double Cost() => _probe.Of(LlmUsageMetrics.CostCounterName).Sum(m => m.Value);

    // ---- 受け入れ基準 1: 上流の途中切断 -----------------------------------------------------------

    [Fact]
    public async Task 上流が途中で切れたとき判明分のトークンを最新の累計で1回計上し不完全として数える()
    {
        var useCase = UseCase(ct => CutAfterStart(ct));

        var events = new List<CompletionStreamEvent>();
        await foreach (var ev in useCase.StreamAsync(Request(), isSynthetic: false, TestContext.Current.CancellationToken))
            events.Add(ev);

        events.Last().Sent.Should().BeFalse();
        events.Last().FailureKind.Should().Be(CompletionFailureKinds.UpstreamError);
        Results().Should().Equal(LlmCompletionMetrics.ResultUpstreamError);
        // 経過報告は累計である。2 回届いた入力 120 を足すと 240 になる（過大計上）。
        Tokens(LlmUsageMetrics.TokenTypeInput).Should().Be(Input);
        Tokens(LlmUsageMetrics.TokenTypeOutput).Should().Be(30);
        Cost().Should().BeApproximately((Input * 3.0 + 30 * 15.0) / 1_000_000, 1e-12);
        var incomplete = Incomplete().Should().ContainSingle().Subject;
        incomplete.Tags[LlmUsageMetrics.IncompleteReasonTag].Should().Be(LlmUsageMetrics.IncompleteUpstreamError);
        incomplete.Tags[LlmUsageMetrics.UsageCoverageTag].Should().Be(LlmUsageMetrics.CoveragePartial);
        incomplete.Tags[LlmCompletionMetrics.PurposeTag].Should().Be("rag-answer");
        incomplete.Tags[LlmCompletionMetrics.ModelTag].Should().Be("test-model");
    }

    // 過大計上の反対側: 送信が成立する前の失敗（1 チャンクも来ていない。429 等）は一括経路と同じく何も積まない。
    [Fact]
    public async Task 送信前に上流が失敗したときはトークンも不完全も積まない()
    {
        var useCase = UseCase(ct => FailImmediately(ct));

        await foreach (var _ in useCase.StreamAsync(Request(), isSynthetic: false, TestContext.Current.CancellationToken))
        {
        }

        Results().Should().Equal(LlmCompletionMetrics.ResultUpstreamError);
        _probe.Of(LlmUsageMetrics.TokensCounterName).Should().BeEmpty();
        Incomplete().Should().BeEmpty();
    }

    // ---- 受け入れ基準 2・4: 利用者の取り消し（CancellationTokenSource） -----------------------------

    [Fact]
    public async Task 利用者が取り消すとcancelledを1件数え判明分の入力トークンも計上する()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var useCase = UseCase(ct => HangAfterStart(ct));

        var act = async () =>
        {
            await foreach (var ev in useCase.StreamAsync(Request(), isSynthetic: false, cts.Token))
            {
                if (ev.Delta == "途中")
                    await cts.CancelAsync(); // 本文を 1 つ受け取った後で利用者が停止を押す
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        Results().Should().Equal(LlmCompletionMetrics.ResultCancelled); // upstream_error・sent には混ぜない
        Tokens(LlmUsageMetrics.TokenTypeInput).Should().Be(Input);
        Cost().Should().BeApproximately(Input * 3.0 / 1_000_000, 1e-12);
        var incomplete = Incomplete().Should().ContainSingle().Subject;
        incomplete.Tags[LlmUsageMetrics.IncompleteReasonTag].Should().Be(LlmUsageMetrics.IncompleteCancelled);
        incomplete.Tags[LlmUsageMetrics.UsageCoverageTag].Should().Be(LlmUsageMetrics.CoveragePartial);
    }

    [Fact]
    public async Task 判明分が無いうちに取り消されたらcancelledと判明分なしの不完全を数えトークンは積まない()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var useCase = UseCase(ct => HangImmediately(ct));
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = async () =>
        {
            await foreach (var _ in useCase.StreamAsync(Request(), isSynthetic: false, cts.Token))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        Results().Should().Equal(LlmCompletionMetrics.ResultCancelled);
        _probe.Of(LlmUsageMetrics.TokensCounterName).Should().BeEmpty();
        Incomplete().Should().ContainSingle()
            .Which.Tags[LlmUsageMetrics.UsageCoverageTag].Should().Be(LlmUsageMetrics.CoverageNone);
    }

    // 取り消しに伴ってプロバイダが OperationCanceledException 以外（IOException 等）を投げても、upstream_error にしない。
    [Fact]
    public async Task 取り消しの後にプロバイダが別の型の例外を投げてもcancelledとして数える()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var useCase = UseCase(ct => IoFailureOnCancel(ct));

        var act = async () =>
        {
            await foreach (var ev in useCase.StreamAsync(Request(), isSynthetic: false, cts.Token))
            {
                if (ev.Delta == "途中")
                    await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<IOException>();
        Results().Should().Equal(LlmCompletionMetrics.ResultCancelled);
        Tokens(LlmUsageMetrics.TokenTypeInput).Should().Be(Input);
        Incomplete().Should().ContainSingle()
            .Which.Tags[LlmUsageMetrics.IncompleteReasonTag].Should().Be(LlmUsageMetrics.IncompleteCancelled);
    }

    // gRPC の切断等で呼び出し側が列挙を途中で捨てた（DisposeAsync だけが来る）ときも取り消しとして数える。
    [Fact]
    public async Task 呼び出し側が列挙を途中で捨てたときもcancelledを1件数える()
    {
        var useCase = UseCase(ct => HangAfterStart(ct));

        await foreach (var ev in useCase.StreamAsync(Request(), isSynthetic: false, TestContext.Current.CancellationToken))
        {
            if (ev.Delta == "途中")
                break;
        }

        Results().Should().Equal(LlmCompletionMetrics.ResultCancelled);
        Tokens(LlmUsageMetrics.TokenTypeInput).Should().Be(Input);
        Incomplete().Should().ContainSingle();
    }

    // 合成監視は取り消されても費用へ入れない（IADR-0378）。除外の件数だけを積む。
    [Fact]
    public async Task 合成監視が取り消されたときは費用へ入れず除外を数える()
    {
        var useCase = UseCase(ct => HangAfterStart(ct));

        await foreach (var ev in useCase.StreamAsync(Request(), isSynthetic: true, TestContext.Current.CancellationToken))
        {
            if (ev.Delta == "途中")
                break;
        }

        Results().Should().Equal(LlmCompletionMetrics.ResultCancelled);
        _probe.Of(LlmUsageMetrics.TokensCounterName).Should().BeEmpty();
        Incomplete().Should().BeEmpty();
        _probe.Of(LlmUsageMetrics.SyntheticExcludedCounterName).Should().ContainSingle();
    }

    // ---- 受け入れ基準 3: 正常完了は二重に数えない ---------------------------------------------------

    [Fact]
    public async Task 正常完了は経過報告を足さずDoneの実数で1回だけ計上する()
    {
        var useCase = UseCase(_ => Complete());

        var events = new List<CompletionStreamEvent>();
        await foreach (var ev in useCase.StreamAsync(Request(), isSynthetic: false, TestContext.Current.CancellationToken))
            events.Add(ev);

        events.Should().NotContain(e => !e.Done && e.Delta.Length == 0); // 経過報告は利用者へ流さない
        events.Last().Sent.Should().BeTrue();
        events.Last().InputTokens.Should().Be(Input);
        events.Last().OutputTokens.Should().Be(45);
        Results().Should().Equal(LlmCompletionMetrics.ResultSent);
        Tokens(LlmUsageMetrics.TokenTypeInput).Should().Be(Input);  // 経過報告 2 回 + Done を足すと 360 になる
        Tokens(LlmUsageMetrics.TokenTypeOutput).Should().Be(45);
        _probe.Of(LlmUsageMetrics.CostCounterName).Should().ContainSingle();
        Incomplete().Should().BeEmpty();
    }

    // 最終イベントを受け取ってすぐ列挙を捨てても、取り消しとして 2 回目を書かない。
    [Fact]
    public async Task 最終イベントの受信直後に列挙を捨てても取り消しとして二重に数えない()
    {
        var useCase = UseCase(_ => Complete());

        await foreach (var ev in useCase.StreamAsync(Request(), isSynthetic: false, TestContext.Current.CancellationToken))
        {
            if (ev.Done)
                break;
        }

        Results().Should().Equal(LlmCompletionMetrics.ResultSent);
        Tokens(LlmUsageMetrics.TokenTypeInput).Should().Be(Input);
        Incomplete().Should().BeEmpty();
    }

    // 例外なく終わったが Done が無い（契約上あり得る形）。計器は従来どおり sent、費用は判明分＋不完全。
    [Fact]
    public async Task Doneが無いまま終わったら判明分を計上し最終チャンク欠落として数える()
    {
        var useCase = UseCase(_ => EndWithoutDone());

        await foreach (var _ in useCase.StreamAsync(Request(), isSynthetic: false, TestContext.Current.CancellationToken))
        {
        }

        Results().Should().Equal(LlmCompletionMetrics.ResultSent);
        Tokens(LlmUsageMetrics.TokenTypeInput).Should().Be(Input);
        Incomplete().Should().ContainSingle()
            .Which.Tags[LlmUsageMetrics.IncompleteReasonTag].Should().Be(LlmUsageMetrics.IncompleteNoFinalChunk);
    }

    // ---- 筋書き ------------------------------------------------------------------------------------

    private static async IAsyncEnumerable<CompletionChunk> CutAfterStart([EnumeratorCancellation] CancellationToken ct)
    {
        yield return Progress(Input);
        yield return new CompletionChunk("途中");
        yield return Progress(Input, 30);
        await Task.Yield();
        throw new HttpRequestException("connection reset mid-stream", new IOException("reset"));
    }

    private static async IAsyncEnumerable<CompletionChunk> FailImmediately([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        throw new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests);
#pragma warning disable CS0162 // 反復子にするための到達しない yield
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<CompletionChunk> HangAfterStart([EnumeratorCancellation] CancellationToken ct)
    {
        yield return Progress(Input);
        yield return new CompletionChunk("途中");
        await Task.Delay(Timeout.Infinite, ct);
        yield return new CompletionChunk("届かない");
    }

    // 取り消されると接続の切断として IOException を投げる（OperationCanceledException に包まない）プロバイダ。
    private static async IAsyncEnumerable<CompletionChunk> IoFailureOnCancel([EnumeratorCancellation] CancellationToken ct)
    {
        yield return Progress(Input);
        yield return new CompletionChunk("途中");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            throw new IOException("connection aborted by cancellation");
        }

        yield return new CompletionChunk("届かない");
    }

    private static async IAsyncEnumerable<CompletionChunk> HangImmediately([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield return new CompletionChunk("届かない");
    }

    private static async IAsyncEnumerable<CompletionChunk> Complete()
    {
        yield return Progress(Input);
        yield return new CompletionChunk("回答");
        yield return Progress(Input, 40);
        await Task.Yield();
        yield return new CompletionChunk(string.Empty, Done: true, Input, 45, CompletionStopReasons.EndTurn);
    }

    private static async IAsyncEnumerable<CompletionChunk> EndWithoutDone()
    {
        yield return Progress(Input);
        yield return new CompletionChunk("本文");
        await Task.Yield();
    }
}
