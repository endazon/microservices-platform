using AwesomeAssertions;
using LlmGateway.Domain.Pricing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Domain.Pricing;

// FR-10, NFR, ADR-0006, ADR-0044 決定 3 (#443): 有効期間つき単価表。
// **境界（切替時刻ちょうど・その前後）を固定する**のがこのクラスの主眼である ——
// 導入価格の終了日を反映し忘れると試算は**エラーを出さずに過小**になる、という失敗の形が
// ADR-0044 のコンテキストそのものだからである。
[Trait("TestKind", "Unit")]
public class ModelPriceTableTests
{
    // **合成の単価改定**（仕組みの検査用。実価格ではない）: $2/$10 → 2026-09-01 から $3/$15。
    // ［2026-10-05 / #1741］ADR-0044 §コンテキストはこれを claude-sonnet-5 の実例としていたが、その引き上げは
    // 中止された（2026-09-09 訂正）。実配備の単価は DeployedPriceTableTests が固定する。
    // **区間は半開 [From, To) であり、切替時刻ちょうどは新単価側に属する。**
    private static readonly DateTimeOffset Switch = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static ModelPriceTable Table(ModelPricingOptions? options = null, ILogger<ModelPriceTable>? logger = null)
        => new(new StaticOptionsMonitor<ModelPricingOptions>(options ?? SonnetTable()),
               logger ?? NullLogger<ModelPriceTable>.Instance);

    private static ModelPricingOptions SonnetTable() => new()
    {
        Currency = "USD",
        Models =
        {
            ["claude-sonnet-5"] =
            [
                new ModelPriceEntry
                {
                    EffectiveFrom = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                    EffectiveTo = Switch,
                    InputPerMillionTokens = 2.0m,
                    OutputPerMillionTokens = 10.0m,
                },
                new ModelPriceEntry
                {
                    EffectiveFrom = Switch,
                    InputPerMillionTokens = 3.0m,
                    OutputPerMillionTokens = 15.0m,
                },
            ],
        },
    };

    // FR-10, ADR-0044 決定 3 (T-1): 区間の内側では当該区間の単価が当たる。
    [Fact]
    public void 区間の内側では当該区間の単価が適用される()
    {
        var result = Table().Estimate("claude-sonnet-5", 1_000_000, 1_000_000, Switch.AddDays(-10));

        result.Status.Should().Be(PricingStatus.Priced);
        result.Cost.Should().Be(12.0m); // 2.0 + 10.0
    }

    // FR-10, ADR-0044 決定 3 (T-2): **切替時刻ちょうどは新単価側**（EffectiveFrom は含む・EffectiveTo は含まない）。
    // ここが逆だと、同一時刻に 2 区間が該当するか、どちらにも該当しない穴が空く。
    [Fact]
    public void 切替時刻ちょうどは新しい単価が適用される()
    {
        var result = Table().Estimate("claude-sonnet-5", 1_000_000, 1_000_000, Switch);

        result.Status.Should().Be(PricingStatus.Priced);
        result.Cost.Should().Be(18.0m); // 3.0 + 15.0
    }

    // FR-10, ADR-0044 決定 3 (T-3): 切替の直前 1 tick は**旧単価**である。
    [Fact]
    public void 切替直前は旧い単価が適用される()
    {
        var result = Table().Estimate("claude-sonnet-5", 1_000_000, 1_000_000, Switch.AddTicks(-1));

        result.Status.Should().Be(PricingStatus.Priced);
        result.Cost.Should().Be(12.0m);
    }

    // FR-10, ADR-0044 決定 3 (T-4): 期間をまたぐ集計でも、**呼び出しごとにその時点の単価**が当たる。
    // 集計側が期間全体に 1 つの単価を掛けないことを固定する。
    [Fact]
    public void 期間をまたぐ集計は呼び出し時点の単価で按分される()
    {
        var table = Table();

        var before = table.Estimate("claude-sonnet-5", 1_000_000, 0, Switch.AddHours(-1));
        var after = table.Estimate("claude-sonnet-5", 1_000_000, 0, Switch.AddHours(1));

        (before.Cost + after.Cost).Should().Be(5.0m); // 2.0（旧）+ 3.0（新）
    }

    // FR-10, ADR-0044 決定 3 (T-5): どの区間にも該当しない時刻は **OutOfEffectivePeriod**。
    // 🔴 **0 円で成功させない** —— 期限切れが「費用の減少」に化けて増加の検知をすり抜ける。
    [Fact]
    public void どの区間にも該当しない時刻は期間外として返る()
    {
        var result = Table().Estimate(
            "claude-sonnet-5", 1_000_000, 1_000_000, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        result.Status.Should().Be(PricingStatus.OutOfEffectivePeriod);
        result.IsPriced.Should().BeFalse();
    }

    // FR-10, ADR-0044 決定 3 (T-6): 単価が 1 件も無いモデルは **NoEntryForModel**（無音の 0 円にしない）。
    [Fact]
    public void 単価が登録されていないモデルは該当なしとして返る()
    {
        var result = Table().Estimate("gpt-5", 1_000, 1_000, Switch);

        result.Status.Should().Be(PricingStatus.NoEntryForModel);
        result.IsPriced.Should().BeFalse();
    }

    // FR-10, ADR-0044 決定 3 (T-7): 入力と出力は**別々の単価**で按分される（百万トークンあたり）。
    [Fact]
    public void 入力と出力は別々の単価で按分される()
    {
        var result = Table().Estimate("claude-sonnet-5", 500_000, 100_000, Switch);

        // 0.5M × $3 + 0.1M × $15 = 1.5 + 1.5
        result.Cost.Should().Be(3.0m);
    }

    // FR-10, ADR-0044 決定 3 (T-8): モデル名の大小文字は区別しない（設定の綴り揺れで単価が消えない）。
    [Fact]
    public void モデル名の大小文字は区別しない()
        => Table().Estimate("CLAUDE-SONNET-5", 1_000_000, 0, Switch).Status
            .Should().Be(PricingStatus.Priced);

    // NFR-19, FR-10, ADR-0044 決定 3 (T-41, #1743): 期間外の時刻は**警告ログを 1 件出す**（モデル名を含む）。
    // 🔴 決定 3 は「期間外の単価で試算した場合は警告を出す」と定める。状態だけを見る T-5 では、
    // 警告を消しても緑のままだった（#1743 の変異 M3b）。
    [Fact]
    public void 期間外の時刻は警告ログを1件出す()
    {
        var logger = new RecordingLogger<ModelPriceTable>();

        var result = Table(logger: logger).Estimate(
            "claude-sonnet-5", 1_000, 1_000, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        result.Status.Should().Be(PricingStatus.OutOfEffectivePeriod);
        var warning = logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("claude-sonnet-5");
    }

    // NFR-19, FR-10, ADR-0044 決定 3 (T-41, #1743): 未登録のモデルも同様に**警告ログを 1 件出す**（対照）。
    [Fact]
    public void 未登録のモデルは警告ログを1件出す()
    {
        var logger = new RecordingLogger<ModelPriceTable>();

        var result = Table(logger: logger).Estimate("gpt-5", 1_000, 1_000, Switch);

        result.Status.Should().Be(PricingStatus.NoEntryForModel);
        var warning = logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("gpt-5");
    }

    // NFR-19 (T-41 の陰性対照, #1743): 単価が解決できた呼び出しは警告を出さない
    // （常に警告する実装を T-41 だけでは落とせないため）。
    [Fact]
    public void 単価が解決できた呼び出しは警告ログを出さない()
    {
        var logger = new RecordingLogger<ModelPriceTable>();

        Table(logger: logger).Estimate("claude-sonnet-5", 1_000, 1_000, Switch)
            .Status.Should().Be(PricingStatus.Priced);
        logger.Entries.Should().BeEmpty();
    }

    // FR-11, ADR-0044 決定 3, IADR-0529 (#1875): プロンプト長で単価が 2 段のモデル（claude-haiku-5-5 の形）。
    private static ModelPricingOptions TieredTable() => new()
    {
        Models =
        {
            ["claude-haiku-5-5"] =
            [
                new ModelPriceEntry
                {
                    InputPerMillionTokens = 0.10m,
                    OutputPerMillionTokens = 0.50m,
                    LongPrompt = new LongPromptPrice
                    {
                        ThresholdInputTokens = 100_000,
                        InputPerMillionTokens = 0.50m,
                        OutputPerMillionTokens = 2.50m,
                    },
                },
            ],
        },
    };

    // FR-10 T-46 (IADR-0529): 段は**その要求の入力トークン数**で決まり、境界ちょうど（100,000）は下段、1 つ超えると上段。
    // 上段は入力・出力の**両方**に効く。境界の比較を >= に変えても、上段の出力単価を下段のまま残しても赤くなる。
    [Theory]
    [InlineData(100_000L, 1_000_000L, false, 0.01, 0.50)]      // 境界ちょうど: 下段（入力 0.10 / 出力 0.50）
    [InlineData(100_001L, 1_000_000L, true, 0.0500005, 2.50)] // 境界 +1: 上段（入力 0.50 / 出力 2.50）
    [InlineData(1L, 0L, false, 0.0000001, 0)]                  // 小さな要求: 下段
    public void プロンプト長で単価の段を要求ごとに選ぶ(
        long inputTokens, long outputTokens, bool longPrompt, double inputCost, double outputCost)
    {
        var result = Table(TieredTable()).Estimate("claude-haiku-5-5", inputTokens, outputTokens, Switch);

        result.Status.Should().Be(PricingStatus.Priced);
        result.LongPromptApplied.Should().Be(longPrompt);
        result.Cost.Should().Be((decimal)inputCost + (decimal)outputCost);
    }

    // FR-10 T-47 (IADR-0529): 上段を持たない単価（1 段）は入力がどれほど大きくても従前どおり 1 段で換算する（回帰防止）。
    [Fact]
    public void 上段を持たない単価は入力の大きさによらず1段()
    {
        var result = Table().Estimate("claude-sonnet-5", 1_000_000, 0, Switch.AddDays(-1));

        result.LongPromptApplied.Should().BeFalse();
        result.Cost.Should().Be(2.0m);
    }

    // ログ出力を検証するための最小のロガー（OpenAiProviderStopReasonTests と同型）。
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    // テスト用の固定 IOptionsMonitor（設定変更の通知は使わない）。
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
