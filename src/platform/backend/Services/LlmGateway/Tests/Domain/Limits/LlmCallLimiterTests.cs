using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Limits;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using LlmGateway.Features.Completions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Tests.Domain.Limits;

// NFR-18, NFR-21, ADR-0044, IADR-0672 (#2404): 送る前の上限の判定器（LlmCallLimiter）の単体試験。
// 時刻は FakeTimeProvider で進める（60 秒の窓・UTC の日付の切り替わり）。
// 計器は共有 Meter へ発行するので SharedMeterCollection に入る（IADR-0394）。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Unit")]
public sealed class LlmCallLimiterTests : IDisposable
{
    private const string Caller = "retrieval-service";
    private const string Model = "test-model";

    private readonly ServiceProvider _meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 23, 0, 0, TimeSpan.Zero));
    private readonly LlmRoutingOptions _routing = new();
    private readonly ModelPriceTable _prices;
    private readonly MeterListener _listener = new();
    private readonly List<Dictionary<string, string>> _exceeded = [];

    public LlmCallLimiterTests()
    {
        _routing.PurposeModels["rag-answer"] = Model;
        _routing.PurposeModels["analysis"] = Model;
        var pricing = new ModelPricingOptions();
        // 合成のモデル名と任意の単価（実価格ではない。入力 3・出力 15 / 百万トークン）。
        pricing.Models[Model] = [new ModelPriceEntry { InputPerMillionTokens = 3.0m, OutputPerMillionTokens = 15.0m }];
        _prices = new ModelPriceTable(
            new TestClaudeProviders.StaticOptionsMonitor<ModelPricingOptions>(pricing), NullLogger<ModelPriceTable>.Instance);

        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LlmCallLimitMetrics.MeterName
                && instrument.Name == LlmCallLimitMetrics.ExceededCounterName)
                l.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var d = new Dictionary<string, string>();
            foreach (var t in tags) d[t.Key] = t.Value?.ToString() ?? string.Empty;
            lock (_exceeded) _exceeded.Add(d);
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _meterServices.Dispose();
    }

    private LlmCallLimiter Limiter(Action<LlmCallLimitOptions>? configure = null)
    {
        var o = new LlmCallLimitOptions();
        configure?.Invoke(o);
        return TestCallLimiters.Create(_meterServices.GetRequiredService<IMeterFactory>(), _routing, _prices, _time, o);
    }

    private IReadOnlyList<Dictionary<string, string>> Exceeded() { lock (_exceeded) return [.. _exceeded]; }

    private static LlmCallAdmission Admit(LlmCallLimiter l, string purpose = "rag-answer", int promptChars = 1000, int maxTokens = 1000, string? caller = Caller)
        => l.Admit(caller, purpose, Model, promptChars, maxTokens);

    // ---- 段 -------------------------------------------------------------------------------------

    [Fact]
    public void Reportでは超えても止めず_種類と段と呼び手と用途を計器に載せる()
    {
        var l = Limiter(o => o.MaxOutputTokens = 10);

        var a = Admit(l, maxTokens: 11);

        a.Denied.Should().BeFalse();
        a.Exceeded.Should().Equal(LlmCallLimiter.KindMaxTokens);
        var m = Exceeded().Should().ContainSingle().Subject;
        m[LlmCallLimitMetrics.KindTag].Should().Be("max_tokens");
        m[LlmCallLimitMetrics.ModeTag].Should().Be("report");
        m[LlmCallLimitMetrics.CallerTag].Should().Be(Caller);
        m[LlmCompletionMetrics.PurposeTag].Should().Be("rag-answer");
    }

    [Fact]
    public void Enforceでは超えた呼び出しを止め_止めた呼び出しは要求数の枠を消費しない()
    {
        var l = Limiter(o =>
        {
            o.Mode = LlmCallLimitMode.Enforce;
            o.MaxOutputTokens = 10;
            o.Default.PurposeRequestsPerMinute = 1;
        });

        Admit(l, maxTokens: 11).Denied.Should().BeTrue();
        Exceeded().Should().ContainSingle().Which[LlmCallLimitMetrics.ModeTag].Should().Be("enforce");

        // 止めた 1 件は枠に入っていないので、次の正しい呼び出しは通る。
        Admit(l, maxTokens: 10).Denied.Should().BeFalse();
        Admit(l, maxTokens: 10).Exceeded.Should().Equal(LlmCallLimiter.KindPurposeRate);
    }

    // ---- 要求数 ----------------------------------------------------------------------------------

    [Fact]
    public void 要求数の枠は60秒で空く()
    {
        var l = Limiter(o => { o.Mode = LlmCallLimitMode.Enforce; o.Default.PurposeRequestsPerMinute = 1; });

        Admit(l).Denied.Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(59));
        Admit(l).Denied.Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(1));
        Admit(l).Denied.Should().BeFalse();
    }

    [Fact]
    public void 呼び手の名前が無い呼び出しはunknownの1つの枠に入る()
    {
        var l = Limiter(o => { o.Mode = LlmCallLimitMode.Enforce; o.Default.PurposeRequestsPerMinute = 1; });

        Admit(l, caller: null).Denied.Should().BeFalse();
        Admit(l, caller: " ").Denied.Should().BeTrue();
        Exceeded().Single()[LlmCallLimitMetrics.CallerTag].Should().Be(LlmCallLimiter.UnknownCaller);
    }

    // ---- 費用 ------------------------------------------------------------------------------------

    // 見込み額: 入力 1,000 文字 × 3 / 1M ＋ 出力 1,000 × 15 / 1M = 0.003 ＋ 0.015 = 0.018。
    private const decimal Estimate1000 = 0.018m;

    [Fact]
    public void 当日の費用は見込み額を含めて判定し_送った後は実額に置き換わる()
    {
        // 上限 0.03: 1 件目の見込み 0.018 は通り、見込みのままなら 2 件目（0.018 ＋ 0.018 = 0.036）は超える。
        var l = Limiter(o => { o.Mode = LlmCallLimitMode.Enforce; o.Default.DailyCostLimit = 0.03m; });

        var first = Admit(l);
        first.Denied.Should().BeFalse();
        Admit(l).Exceeded.Should().Equal(LlmCallLimiter.KindCallerCost);

        // 実額（入力 100・出力 100 = 0.0003 ＋ 0.0015 = 0.0018）へ置き換えると、2 件目（0.0018 ＋ 0.018）は通る。
        first.Settle(Model, 100, 100);
        Admit(l).Denied.Should().BeFalse();
    }

    [Fact]
    public void 送らなかった呼び出しの見込み額は戻る_SettleとReleaseは最初の1回だけが効く()
    {
        var l = Limiter(o => { o.Mode = LlmCallLimitMode.Enforce; o.Default.DailyCostLimit = Estimate1000; });

        var first = Admit(l);
        first.Release();
        first.Settle(Model, 1_000_000, 1_000_000); // Release の後なので効かない
        first.Release();

        Admit(l).Denied.Should().BeFalse("見込みは戻っているので 0.018 ちょうどまで使える");
        Admit(l).Exceeded.Should().Equal(LlmCallLimiter.KindCallerCost);
    }

    [Fact]
    public void 用途ごとの費用の上限は呼び手の合計とは別に効く()
    {
        var l = Limiter(o =>
        {
            o.Mode = LlmCallLimitMode.Enforce;
            o.Default.PurposeDailyCostLimits["rag-answer"] = Estimate1000;
        });

        Admit(l, purpose: "rag-answer").Denied.Should().BeFalse();
        Admit(l, purpose: "rag-answer").Exceeded.Should().Equal(LlmCallLimiter.KindPurposeCost);
        Admit(l, purpose: "analysis").Denied.Should().BeFalse("analysis には用途の上限が無い");
    }

    [Fact]
    public void UTCの日付が変わると費用の枠は0に戻る()
    {
        var l = Limiter(o => { o.Mode = LlmCallLimitMode.Enforce; o.Default.DailyCostLimit = Estimate1000; });

        Admit(l).Settle(Model, 1000, 1000);
        Admit(l).Denied.Should().BeTrue();

        _time.Advance(TimeSpan.FromHours(1)); // 2026-11-06T00:00Z
        Admit(l).Denied.Should().BeFalse();
    }

    // ---- 判定器へ組み込んだ形（逐次の経路） ----------------------------------------------------------

    private sealed class CountingProvider : ILlmProvider
    {
        public int Calls;

        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new CompletionResult("ok", 1, 1, CompletionStopReasons.EndTurn));
        }

        public async IAsyncEnumerable<CompletionChunk> StreamAsync(
            CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Yield();
            yield return new CompletionChunk("ok", Done: false);
            yield return new CompletionChunk(string.Empty, Done: true, InputTokens: 1, OutputTokens: 1);
        }
    }

    private sealed class FixedRouter(RoutingDecision decision) : ILlmRouter
    {
        public RoutingDecision Route(RoutingRequest request) => decision;
    }

    private (CompletionUseCase UseCase, CountingProvider Provider) UseCase(LlmCallLimiter limiter)
    {
        var provider = new CountingProvider();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ILlmProvider>("claude", provider);
        var meterFactory = _meterServices.GetRequiredService<IMeterFactory>();
        var routing = new TestClaudeProviders.StaticOptionsMonitor<LlmRoutingOptions>(_routing);
        var useCase = new CompletionUseCase(
            new FixedRouter(new RoutingDecision(true, "claude-managed", "claude", ProtectionTier.B, Model, false, "test")),
            services.BuildServiceProvider(),
            NullLoggerFactory.Instance,
            new LlmCompletionMetrics(meterFactory, routing),
            new LlmUsageMetrics(meterFactory, routing, _prices, _time),
            new LogOccurrenceThrottle(_time),
            limiter);
        return (useCase, provider);
    }

    [Fact]
    public async Task 逐次でもEnforceで超えた呼び出しは送らず_done_sent_falseの1イベントで返す()
    {
        var (useCase, provider) = UseCase(Limiter(o => { o.Mode = LlmCallLimitMode.Enforce; o.MaxOutputTokens = 10; }));

        var events = new List<CompletionStreamEvent>();
        await foreach (var ev in useCase.StreamAsync(
                           new CompletionApiRequest("本文", MaxTokens: 11, Purpose: "rag-answer"),
                           isSynthetic: false, TestContext.Current.CancellationToken, caller: Caller))
            events.Add(ev);

        provider.Calls.Should().Be(0);
        var last = events.Should().ContainSingle().Subject;
        last.Done.Should().BeTrue();
        last.Sent.Should().BeFalse();
        last.FailureKind.Should().Be(CompletionFailureKinds.LimitExceeded);
    }

    [Fact]
    public async Task 逐次の完了は見込み額を実額へ置き換える()
    {
        var limiter = Limiter(o => { o.Mode = LlmCallLimitMode.Enforce; o.Default.DailyCostLimit = 0.03m; });
        var (useCase, provider) = UseCase(limiter);

        await foreach (var _ in useCase.StreamAsync(
                           new CompletionApiRequest(new string('a', 1000), MaxTokens: 1000, Purpose: "rag-answer"),
                           isSynthetic: false, TestContext.Current.CancellationToken, caller: Caller))
        {
        }

        provider.Calls.Should().Be(1);
        // 実額（1・1 トークン）へ置き換わっているので、見込み 0.018 の 2 件目は通る（見込みのままなら 0.036 で超える）。
        Admit(limiter).Denied.Should().BeFalse();
    }
}
