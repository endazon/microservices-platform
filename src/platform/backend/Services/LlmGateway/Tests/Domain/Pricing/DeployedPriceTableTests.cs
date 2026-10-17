using AwesomeAssertions;
using LlmGateway.Domain.Pricing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests.Domain.Pricing;

// NFR-19, ADR-0044 (#1741): **実配備の appsettings.json** の単価表を、ホストの束縛を通して検査する。
//
// 計画 ADR-0044 の 2026-09-09 訂正: 提供元は claude-sonnet-5 の $2/$10 を標準価格とし、
// 2026-09-01 に予定されていた $3/$15 への引き上げは行われない。単価表が引き上げ後の区間を
// 持ったままだと、試算は**エラーを出さずに過大**になる（#1741 で実際に起きた）。
//
// **実設定を通すことに意味がある** —— 合成 config の境界テスト（ModelPriceTableTests）は
// 仕組みを固定するが、設定の値そのものの誤りは捕まえない。
[Trait("TestKind", "Integration")]
public class DeployedPriceTableTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    public static TheoryData<DateTimeOffset> SonnetInstants() =>
    [
        new DateTimeOffset(2026, 8, 31, 23, 59, 59, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), // 中止された改定の予定時刻ちょうど
        new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero),
    ];

    // NFR-19, ADR-0044 (#1741): sonnet の単価は 2026-09-01 の前後を問わず入力 $2 / 出力 $10（百万トークンあたり）。
    // ［2026-10-17 / #1903］#1741 の対象だった claude-sonnet-5 の行は、計画 ADR-0138 決定 7 により単価表から外した。
    // 「予定日の後ろに区切りを持たない（中止された改定の区間を持たない）」ことの固定は、後継の claude-sonnet-5-5 で引き継ぐ。
    [Theory]
    [MemberData(nameof(SonnetInstants))]
    public void 実設定のclaude_sonnet_5_5は改定予定日の前後とも2ドルと10ドルで換算される(DateTimeOffset at)
    {
        var prices = factory.Services.GetRequiredService<ModelPriceTable>();

        var input = prices.Estimate("claude-sonnet-5-5", 1_000_000, 0, at);
        var output = prices.Estimate("claude-sonnet-5-5", 0, 1_000_000, at);

        prices.Currency.Should().Be("USD");
        input.Status.Should().Be(PricingStatus.Priced);
        input.Cost.Should().Be(2.0m);
        output.Status.Should().Be(PricingStatus.Priced);
        output.Cost.Should().Be(10.0m);
    }

    // FR-10 T-48, FR-11, ADR-0044 決定 3, IADR-0531 (#1875・planning#783): 実設定の 5.5 系の単価（提供元の公表値。2026-10-10 確認）。
    // 入力 / 出力の百万トークンあたり: opus-5-5 $4 / $20、sonnet-5-5 $2 / $10、
    // haiku-5-5 はプロンプト 100,000 トークン以下 $0.10 / $0.50、超 $0.50 / $2.50。
    [Theory]
    [InlineData("claude-opus-5-5", 1_000L, 4.0, 20.0, false)]
    [InlineData("claude-sonnet-5-5", 1_000L, 2.0, 10.0, false)]
    [InlineData("claude-haiku-5-5", 1_000L, 0.10, 0.50, false)]
    [InlineData("claude-haiku-5-5", 100_000L, 0.10, 0.50, false)]
    [InlineData("claude-haiku-5-5", 100_001L, 0.50, 2.50, true)]
    public void 実設定の55系の単価で換算される(
        string model, long inputTokens, double inputPerMillion, double outputPerMillion, bool longPrompt)
    {
        var prices = factory.Services.GetRequiredService<ModelPriceTable>();
        var at = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

        var result = prices.Estimate(model, inputTokens, 1_000_000, at);

        result.Status.Should().Be(PricingStatus.Priced);
        result.LongPromptApplied.Should().Be(longPrompt);
        result.Cost.Should().Be(inputTokens / 1_000_000m * (decimal)inputPerMillion + (decimal)outputPerMillion);
    }

    // FR-10 T-48, FR-11, ADR-0138 決定 7 (#1903): 実設定の単価表は 5.5 系の 3 モデルだけを持つ。
    // 旧 ID の行を残さない（決定 7「単価表に旧 ID の行を残さない」）。有効期間つきで残す形も採らない（同）。
    [Fact]
    public void 実設定の単価表は55系の3モデルだけを持つ()
    {
        var options = factory.Services.GetRequiredService<IOptions<ModelPricingOptions>>().Value;

        options.Models.Keys.Should().BeEquivalentTo(["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"]);
    }

    // FR-10 T-48, ADR-0138 決定 7 (#1903): 旧世代のモデルは単価を持たない（NoEntryForModel）。
    // ［2026-10-17］従前（IADR-0531 決定 1）は切り戻し用に許可集合へ残した旧モデルの単価を固定していた。決定 7 で許可集合からも
    // 外れたので、ゲートウェイがこれらで呼ぶことは無い（明示の Model で要求しても許可集合の外として扱われる。T-42）。
    [Theory]
    [InlineData("claude-opus-5")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-haiku-4-5")]
    [InlineData("claude-opus-4-8")]
    [InlineData("claude-sonnet-4-6")]
    public void 実設定は旧世代のモデルの単価を持たない(string model)
    {
        var prices = factory.Services.GetRequiredService<ModelPriceTable>();

        var result = prices.Estimate(model, 1_000_000, 0, new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero));

        result.Status.Should().Be(PricingStatus.NoEntryForModel);
    }
}
