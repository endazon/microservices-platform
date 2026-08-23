using AwesomeAssertions;
using LlmGateway.Api.Foundation.Pricing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Api.Tests;

// FR-10, NFR, ADR-0006, ADR-0044 決定 3 (#443): 有効期間つき単価表。
// **境界（切替時刻ちょうど・その前後）を固定する**のがこのクラスの主眼である ——
// 導入価格の終了日を反映し忘れると試算は**エラーを出さずに過小**になる、という失敗の形が
// ADR-0044 のコンテキストそのものだからである。
public class ModelPriceTableTests
{
    // 単価改定の実例（ADR-0044 §コンテキスト）: claude-sonnet-5 の $2/$10 は 2026-08-31 まで、
    // 9 月以降 $3/$15。**区間は半開 [From, To) であり、切替時刻ちょうどは新単価側に属する。**
    private static readonly DateTimeOffset Switch = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static ModelPriceTable Table(ModelPricingOptions? options = null)
        => new(new StaticOptionsMonitor<ModelPricingOptions>(options ?? SonnetTable()),
               NullLogger<ModelPriceTable>.Instance);

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

    // テスト用の固定 IOptionsMonitor（設定変更の通知は使わない）。
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
