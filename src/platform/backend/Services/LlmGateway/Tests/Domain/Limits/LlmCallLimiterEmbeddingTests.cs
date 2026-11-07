using System.Diagnostics.Metrics;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Authorization;
using LlmGateway.Domain.Limits;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LlmGateway.Tests.Domain.Limits;

// NFR-18, NFR-21, ADR-0044, IADR-0678, IADR-0701: 埋め込みの送る前の上限（LlmCallLimiter.AdmitEmbedding）の単体試験。
// 埋め込みも補完と同じ判定器を通り、呼び手の要求数・当日の費用の枠を補完と合算する（用途の枠は固定の embedding）。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Unit")]
public sealed class LlmCallLimiterEmbeddingTests : IDisposable
{
    private const string Caller = "ingestion-service";
    private const string CompletionModel = "test-model";
    private const string EmbeddingModel = "test-embedding";

    private readonly ServiceProvider _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly LlmRoutingOptions _routing = new();
    private readonly ModelPriceTable _prices;
    private readonly MeterListener _listener = new();
    private readonly List<Dictionary<string, string>> _exceeded = [];

    public LlmCallLimiterEmbeddingTests()
    {
        _routing.PurposeModels["rag-answer"] = CompletionModel;
        var pricing = new ModelPricingOptions();
        // 合成のモデル名と任意の単価（実価格ではない）。
        pricing.Models[CompletionModel] = [new ModelPriceEntry { InputPerMillionTokens = 3.0m, OutputPerMillionTokens = 15.0m }];
        _prices = new ModelPriceTable(
            new TestClaudeProviders.StaticOptionsMonitor<ModelPricingOptions>(pricing), NullLogger<ModelPriceTable>.Instance);

        var meter = _meters.GetRequiredService<IMeterFactory>().Create(LlmCallLimitMetrics.MeterName);
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == LlmCallLimitMetrics.ExceededCounterName)
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
        _meters.Dispose();
    }

    private LlmCallLimiter Limiter(Action<LlmCallLimitOptions> configure)
    {
        var o = new LlmCallLimitOptions();
        // 合成の単価（入力 2 / 百万トークン。実価格ではない）。
        o.EmbeddingPrices[EmbeddingModel] = 2m;
        // 費用の試験で大きな入力を使うので、長さの上限は既定より上げる（長さの試験は自分で下げる）。
        o.MaxPromptChars = 10_000_000;
        configure(o);
        return TestCallLimiters.Create(_meters.GetRequiredService<IMeterFactory>(), _routing, _prices, _time, o);
    }

    private IReadOnlyList<Dictionary<string, string>> Exceeded() { lock (_exceeded) return [.. _exceeded]; }

    [Fact]
    public void 段2では埋め込みの要求数の上限を超えたら送らず_種類と用途embeddingを計器に載せる()
    {
        var l = Limiter(o =>
        {
            o.EmbeddingMode = LlmGuardStage.Enforce;
            o.Default.PurposeRequestsPerMinute = 2;
        });

        l.AdmitEmbedding(Caller, EmbeddingModel, 100, billable: true).Denied.Should().BeFalse();
        l.AdmitEmbedding(Caller, EmbeddingModel, 100, billable: true).Denied.Should().BeFalse();
        var third = l.AdmitEmbedding(Caller, EmbeddingModel, 100, billable: true);

        third.Denied.Should().BeTrue();
        third.Exceeded.Should().Equal(LlmCallLimiter.KindPurposeRate);
        var m = Exceeded().Should().ContainSingle().Subject;
        m[LlmCallLimitMetrics.ModeTag].Should().Be("enforce");
        m[LlmCompletionMetrics.PurposeTag].Should().Be("embedding");
        m[LlmCallLimitMetrics.CallerTag].Should().Be(Caller);

        // 60 秒の窓が過ぎれば通る。
        _time.Advance(TimeSpan.FromSeconds(61));
        l.AdmitEmbedding(Caller, EmbeddingModel, 100, billable: true).Denied.Should().BeFalse();
    }

    [Fact]
    public void 段1では超えても送り_Offでは判定も計上もしない()
    {
        var report = Limiter(o => o.Default.PurposeRequestsPerMinute = 1);
        report.AdmitEmbedding(Caller, EmbeddingModel, 100, billable: true).Denied.Should().BeFalse();
        var over = report.AdmitEmbedding(Caller, EmbeddingModel, 100, billable: true);
        over.Denied.Should().BeFalse("段 1 は送信を止めない");
        over.Exceeded.Should().Equal(LlmCallLimiter.KindPurposeRate);

        var off = Limiter(o =>
        {
            o.EmbeddingMode = LlmGuardStage.Off;
            o.Mode = LlmCallLimitMode.Enforce;
            o.Default.DailyCostLimit = 0.001m;
            o.Default.RequestsPerMinute = 1;
        });
        for (var i = 0; i < 3; i++)
            off.AdmitEmbedding(Caller, EmbeddingModel, 1_000_000, billable: true).Exceeded.Should().BeEmpty();
        // Off の埋め込みは補完の枠（呼び手の要求数 1・当日の費用 0.001）も消費しない（補完は通る）。
        off.Admit(Caller, "rag-answer", CompletionModel, 1, 1).Denied.Should().BeFalse();
    }

    [Fact]
    public void 段2では入力の長さの上限を超えた埋め込みを送らない()
    {
        var l = Limiter(o =>
        {
            o.EmbeddingMode = LlmGuardStage.Enforce;
            o.MaxPromptChars = 10;
        });

        var a = l.AdmitEmbedding(Caller, EmbeddingModel, 11, billable: true);
        a.Denied.Should().BeTrue();
        a.Exceeded.Should().Equal(LlmCallLimiter.KindPromptLength);
        l.AdmitEmbedding(Caller, EmbeddingModel, 10, billable: true).Denied.Should().BeFalse();
    }

    // 🔴 呼び手の当日の費用は補完と埋め込みの合計（20 USD の合計の枠が埋め込みで抜けない）。
    [Fact]
    public void 埋め込みの費用は補完と同じ呼び手の当日の枠に合算する()
    {
        var l = Limiter(o =>
        {
            o.Mode = LlmCallLimitMode.Enforce;
            o.EmbeddingMode = LlmGuardStage.Enforce;
            o.Default.DailyCostLimit = 3m;
        });

        // 埋め込み 1,000,000 文字 × 2 / 百万 = 2.00（実額も同じ文字数で置き換える）。
        var e = l.AdmitEmbedding(Caller, EmbeddingModel, 1_000_000, billable: true);
        e.Denied.Should().BeFalse();
        e.Settle(EmbeddingModel, 1_000_000, 0);

        // 補完の見込み: 入力 400,000 × 3 / 百万 + 出力 30,000 × 15 / 百万 = 1.65 → 合計 3.65 > 3 で止まる。
        var c = l.Admit(Caller, "rag-answer", CompletionModel, 400_000, 30_000);
        c.Denied.Should().BeTrue();
        c.Exceeded.Should().Equal(LlmCallLimiter.KindCallerCost);

        // 逆向き: 補完で使った分も埋め込みの判定に効く（止めた補完は数えないので、残りは 1.00。それを超える埋め込みは止まる）。
        var e2 = l.AdmitEmbedding(Caller, EmbeddingModel, 500_001, billable: true);
        e2.Denied.Should().BeTrue();
        e2.Exceeded.Should().Equal(LlmCallLimiter.KindCallerCost);
        l.AdmitEmbedding(Caller, EmbeddingModel, 500_000, billable: true).Denied.Should().BeFalse();
    }

    [Fact]
    public void 外へ出ない経路の埋め込みは費用を数えず要求数だけ数える()
    {
        var l = Limiter(o =>
        {
            o.EmbeddingMode = LlmGuardStage.Enforce;
            o.Default.DailyCostLimit = 0.000001m;
            o.Default.PurposeRequestsPerMinute = 2;
        });

        l.AdmitEmbedding(Caller, EmbeddingModel, 1_000_000, billable: false).Denied.Should().BeFalse("ティア A は費用を数えない");
        l.AdmitEmbedding(Caller, EmbeddingModel, 1_000_000, billable: false).Denied.Should().BeFalse();
        l.AdmitEmbedding(Caller, EmbeddingModel, 1, billable: false).Exceeded.Should().Equal(LlmCallLimiter.KindPurposeRate);

        // 陰性対照: 外へ出る経路では同じ入力が費用の上限で止まる。
        var billed = Limiter(o =>
        {
            o.EmbeddingMode = LlmGuardStage.Enforce;
            o.Default.DailyCostLimit = 0.000001m;
        });
        billed.AdmitEmbedding(Caller, EmbeddingModel, 1_000_000, billable: true).Exceeded.Should().Equal(LlmCallLimiter.KindCallerCost);
    }

    [Fact]
    public void 埋め込みの単価表に無いモデルは0にせず既定の入力単価で数え_送れなかった見込みは戻す()
    {
        var l = Limiter(o =>
        {
            o.EmbeddingMode = LlmGuardStage.Enforce;
            o.Default.DailyCostLimit = 9m;
        });

        // 表に無いモデル: 1,000,000 文字 × 10（UnknownModelPrice の入力単価）/ 百万 = 10 > 9。
        l.AdmitEmbedding(Caller, "unlisted-embedding", 1_000_000, billable: true).Exceeded.Should().Equal(LlmCallLimiter.KindCallerCost);

        // 送れなかった見込みは戻す（戻さないと次が止まる）。
        var a = l.AdmitEmbedding(Caller, EmbeddingModel, 4_000_000, billable: true); // 8.00
        a.Denied.Should().BeFalse();
        a.Release();
        l.AdmitEmbedding(Caller, EmbeddingModel, 4_000_000, billable: true).Denied.Should().BeFalse();
    }
}
