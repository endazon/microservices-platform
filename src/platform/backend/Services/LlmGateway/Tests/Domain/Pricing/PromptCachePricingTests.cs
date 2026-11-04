using AwesomeAssertions;
using LlmGateway.Domain.Pricing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Domain.Pricing;

// FR-10, ADR-0044 決定 3, IADR-0531 決定 2, IADR-0671 (#2396): キャッシュの書き・読みを含む要求の金額換算。
//
// 入力トークン（引数 inputTokens）は**プロンプト全体**であり、キャッシュの書き・読みはその**うち**である。
// 金額 = 残り × 入力単価 ＋ 書き × 書き単価 ＋ 読み × 読み単価 ＋ 出力 × 出力単価。
// 2 段の単価（claude-haiku-5-5）の判定量は**プロンプト全体**（提供元: 「プロンプト長はキャッシュの読み・書きを含むすべての入力を数える」）。
// 単価は提供元の公表値の形（2026-11-05 確認）を写した合成の表で、実配備の値は DeployedPriceTableTests が固定する。
[Trait("TestKind", "Unit")]
public class PromptCachePricingTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    private static ModelPriceTable Table(ModelPricingOptions options)
        => new(new Static(options), NullLogger<ModelPriceTable>.Instance);

    private static ModelPricingOptions CachedTable() => new()
    {
        Models =
        {
            ["sonnet-like"] =
            [
                new ModelPriceEntry
                {
                    InputPerMillionTokens = 2.0m, OutputPerMillionTokens = 10.0m,
                    CacheWritePerMillionTokens = 2.50m, CacheReadPerMillionTokens = 0.10m,
                },
            ],
            ["haiku-like"] =
            [
                new ModelPriceEntry
                {
                    InputPerMillionTokens = 0.10m, OutputPerMillionTokens = 0.50m,
                    CacheWritePerMillionTokens = 0.125m, CacheReadPerMillionTokens = 0.01m,
                    LongPrompt = new LongPromptPrice
                    {
                        ThresholdInputTokens = 100_000,
                        InputPerMillionTokens = 0.50m, OutputPerMillionTokens = 2.50m,
                        CacheWritePerMillionTokens = 0.625m, CacheReadPerMillionTokens = 0.05m,
                    },
                },
            ],
            ["no-cache-rate"] =
            [
                new ModelPriceEntry { InputPerMillionTokens = 2.0m, OutputPerMillionTokens = 10.0m },
            ],
        },
    };

    // T-2396-10: 残り・書き・読み・出力を各々の単価で数える。
    // 全体 13,000（残り 1,000・書き 2,000・読み 10,000）・出力 500:
    // 1000×2 + 2000×2.5 + 10000×0.10 + 500×10 = 13,000 /1M = $0.013（全体を入力単価で数えると $0.031）。
    [Fact]
    public void キャッシュの書きと読みを各々の単価で換算する()
    {
        var result = Table(CachedTable()).Estimate("sonnet-like", 13_000, 500, At, new PromptCacheTokens(2_000, 10_000));

        result.Status.Should().Be(PricingStatus.Priced);
        result.Cost.Should().Be(0.013m);
    }

    // T-2396-11: 2 段の判定量はプロンプト全体。残り 50,000＋読み 60,000＝110,000 > 100,000 → 上段。
    // 50000×0.50 + 60000×0.05 + 1000×2.50 = 30,500 /1M = $0.0305
    // （残りの 50,000 だけで判定すると下段の $0.0061 になり、過小に数える —— 旧実装の形）。
    [Fact]
    public void 段の判定量はキャッシュの読み書きを含むプロンプト全体()
    {
        var result = Table(CachedTable()).Estimate("haiku-like", 110_000, 1_000, At, new PromptCacheTokens(0, 60_000));

        result.Status.Should().Be(PricingStatus.Priced);
        result.LongPromptApplied.Should().BeTrue();
        result.Cost.Should().Be(0.0305m);
    }

    // T-2396-11: 全体がちょうど境界（100,000）は下段（「100,000 トークン以下」）。書きも下段の書き単価。
    // 残り 40,000×0.10 + 書き 60,000×0.125 = 4,000 + 7,500 = 11,500 /1M = $0.0115。
    [Fact]
    public void 全体が境界ちょうどなら下段の書き単価()
    {
        var result = Table(CachedTable()).Estimate("haiku-like", 100_000, 0, At, new PromptCacheTokens(60_000, 0));

        result.LongPromptApplied.Should().BeFalse();
        result.Cost.Should().Be(0.0115m);
    }

    // T-2396-12: キャッシュのトークンがあるのに書き・読みの単価が無いときは 0 円にせず、単価なしとして返す。
    // 入力単価で代わりに数えない（書きは過小・読みは 20 倍の過大）。
    [Theory]
    [InlineData(1_000L, 0L)]
    [InlineData(0L, 1_000L)]
    public void キャッシュの単価が無いのにキャッシュのトークンがあれば単価なし(long write, long read)
    {
        var result = Table(CachedTable()).Estimate("no-cache-rate", 5_000, 100, At, new PromptCacheTokens(write, read));

        result.Status.Should().Be(PricingStatus.NoCacheRate);
        result.IsPriced.Should().BeFalse();
        result.Cost.Should().Be(0m);
    }

    // T-2396-13: キャッシュを使わない要求は、キャッシュの単価の有無によらず従前と同じ換算（回帰防止）。
    [Theory]
    [InlineData("sonnet-like")]
    [InlineData("no-cache-rate")]
    public void キャッシュを使わない要求は従前どおり(string model)
    {
        var result = Table(CachedTable()).Estimate(model, 1_000_000, 1_000_000, At);

        result.Status.Should().Be(PricingStatus.Priced);
        result.Cost.Should().Be(12.0m);
    }

    private sealed class Static(ModelPricingOptions value) : IOptionsMonitor<ModelPricingOptions>
    {
        public ModelPricingOptions CurrentValue => value;

        public ModelPricingOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ModelPricingOptions, string?> listener) => null;
    }
}
