using System.Diagnostics.Metrics;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests;

// FR-10, NFR, ADR-0006, ADR-0044 決定 1・3 (#443): LLM 利用実績（用途別・モデル別のトークンと金額）。
public class LlmUsageMetricsTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    private sealed record Measured(string Instrument, double Value, Dictionary<string, string> Tags);

    // 指定した Meter の測定を集める（long / double の両方を拾う）。
    private sealed class Probe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measured> _items = [];

        public Probe()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == LlmUsageMetrics.MeterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.Start();
        }

        private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, string>();
            foreach (var tag in tags) dict[tag.Key] = tag.Value?.ToString() ?? string.Empty;
            lock (_items) _items.Add(new Measured(name, value, dict));
        }

        public IReadOnlyList<Measured> Items { get { lock (_items) return [.. _items]; } }
        public void Dispose() => _listener.Dispose();
    }

    private static LlmUsageMetrics Metrics(bool withPrice = true)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var meterFactory = services.BuildServiceProvider().GetRequiredService<IMeterFactory>();

        var pricing = new ModelPricingOptions();
        if (withPrice)
            pricing.Models["claude-sonnet-5"] =
            [
                new ModelPriceEntry { InputPerMillionTokens = 3.0m, OutputPerMillionTokens = 15.0m },
            ];

        var routing = new LlmRoutingOptions();
        routing.PurposeModels["rag-answer"] = "claude-sonnet-5";

        var prices = new ModelPriceTable(
            new Static<ModelPricingOptions>(pricing), NullLogger<ModelPriceTable>.Instance);
        return new LlmUsageMetrics(
            meterFactory, new Static<LlmRoutingOptions>(routing), prices, TimeProvider.System);
    }

    private static RoutingDecision Decision(string model = "claude-sonnet-5")
        => new(true, "claude-managed", "claude", ProtectionTier.B, model, false, "test");

    // FR-10, ADR-0044 決定 1 (T-15): トークンは**用途別・モデル別**に、入出力を属性で分けて計上される。
    // 総額のみの計測を採らなかった決定が、属性として実際に載っていることを固定する。
    [Fact]
    public void トークンは用途別モデル別に入出力を分けて計上される()
    {
        using var probe = new Probe();

        Metrics().RecordUsage(Decision(), "rag-answer", SensitivityClass.Internal, 1_000, 500, At);

        var tokens = probe.Items.Where(m => m.Instrument == LlmUsageMetrics.TokensCounterName).ToList();
        tokens.Should().HaveCount(2);
        tokens.Should().ContainSingle(m =>
            m.Tags[LlmUsageMetrics.TokenTypeTag] == LlmUsageMetrics.TokenTypeInput && m.Value == 1_000);
        tokens.Should().ContainSingle(m =>
            m.Tags[LlmUsageMetrics.TokenTypeTag] == LlmUsageMetrics.TokenTypeOutput && m.Value == 500);
        tokens.Should().OnlyContain(m =>
            m.Tags[LlmCompletionMetrics.PurposeTag] == "rag-answer"
            && m.Tags[LlmCompletionMetrics.ModelTag] == "claude-sonnet-5");
    }

    // FR-10, ADR-0044 決定 3 (T-16): 金額はゲートウェイ側で換算して計上される（Grafana へ単価を渡さない）。
    [Fact]
    public void 金額はゲートウェイ側で換算して計上される()
    {
        using var probe = new Probe();

        Metrics().RecordUsage(Decision(), "rag-answer", SensitivityClass.Internal, 1_000_000, 1_000_000, At);

        var cost = probe.Items.Single(m => m.Instrument == LlmUsageMetrics.CostCounterName);
        cost.Value.Should().Be(18.0); // 3.0 + 15.0
        cost.Tags[LlmUsageMetrics.CurrencyTag].Should().Be("USD");
        cost.Tags[LlmCompletionMetrics.PurposeTag].Should().Be("rag-answer");
    }

    // FR-10, ADR-0044 決定 3 (T-17): 単価を解決できない呼び出しは、**金額を記録せず**警報として計上する。
    // 🔴 否定形が本体である —— 0 円を積むと期限切れが「費用の減少」に化ける。
    [Fact]
    public void 単価を解決できない呼び出しは金額を記録せず警報を計上する()
    {
        using var probe = new Probe();

        Metrics(withPrice: false)
            .RecordUsage(Decision(), "rag-answer", SensitivityClass.Internal, 1_000, 500, At);

        probe.Items.Should().NotContain(m => m.Instrument == LlmUsageMetrics.CostCounterName);
        var unpriced = probe.Items.Single(m => m.Instrument == LlmUsageMetrics.UnpricedCounterName);
        unpriced.Value.Should().Be(1);
        unpriced.Tags[LlmUsageMetrics.PricingStatusTag].Should().Be(LlmUsageMetrics.PricingNoEntry);
        // トークンは単価と無関係に計上される（費用が出せなくても消費量は残す）。
        probe.Items.Should().Contain(m => m.Instrument == LlmUsageMetrics.TokensCounterName);
    }

    // FR-10, ADR-0044 決定 1 (T-18): 用途は設定で値域を閉じ、未定義値は other へ集約する
    // （IADR-0110 の規律の継承。カーディナリティ爆発を費用系の計器へ持ち込まない）。
    [Fact]
    public void 未定義の用途はotherへ集約される()
    {
        using var probe = new Probe();

        Metrics().RecordUsage(Decision(), "未定義の用途", SensitivityClass.Public, 10, 10, At);

        probe.Items.Where(m => m.Instrument == LlmUsageMetrics.TokensCounterName)
            .Should().OnlyContain(m => m.Tags[LlmCompletionMetrics.PurposeTag] == "other");
    }

    // FR-10, NFR (T-19): **メトリクス命名・ラベルの契約**。ダッシュボードが依存する系列名・ラベル名の
    // 意図しない変更をここで落とす（Prometheus 側の名前は OTel の変換規則で `.` → `_`）。
    [Fact]
    public void 系列名とラベル名は契約として固定される()
    {
        LlmUsageMetrics.MeterName.Should().Be("microservices-platform.llm-gateway");
        LlmUsageMetrics.TokensCounterName.Should().Be("llm.tokens.total");
        LlmUsageMetrics.CostCounterName.Should().Be("llm.cost.total");
        LlmUsageMetrics.UnpricedCounterName.Should().Be("llm.pricing.unpriced.total");
        LlmUsageMetrics.TokenTypeTag.Should().Be("llm.token_type");
        LlmUsageMetrics.PricingStatusTag.Should().Be("llm.pricing_status");
        LlmUsageMetrics.CurrencyTag.Should().Be("llm.currency");
        // 既存カウンタと**同じ軸**で読めることが決定 1 の要点であるため、共有する属性名も固定する。
        LlmCompletionMetrics.PurposeTag.Should().Be("llm.purpose");
        LlmCompletionMetrics.ModelTag.Should().Be("llm.model");
        LlmCompletionMetrics.ProviderTag.Should().Be("llm.provider");
        LlmCompletionMetrics.ConfidentialityTag.Should().Be("llm.confidentiality");
    }

    private sealed class Static<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
