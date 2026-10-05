using AwesomeAssertions;
using LlmGateway.Domain.Pricing;
using Microsoft.Extensions.DependencyInjection;

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

    // NFR-19, ADR-0044 (#1741): claude-sonnet-5 は 2026-09-01 の前後を問わず入力 $2 / 出力 $10（百万トークンあたり）。
    [Theory]
    [MemberData(nameof(SonnetInstants))]
    public void 実設定のclaude_sonnet_5は改定予定日の前後とも2ドルと10ドルで換算される(DateTimeOffset at)
    {
        var prices = factory.Services.GetRequiredService<ModelPriceTable>();

        var input = prices.Estimate("claude-sonnet-5", 1_000_000, 0, at);
        var output = prices.Estimate("claude-sonnet-5", 0, 1_000_000, at);

        prices.Currency.Should().Be("USD");
        input.Status.Should().Be(PricingStatus.Priced);
        input.Cost.Should().Be(2.0m);
        output.Status.Should().Be(PricingStatus.Priced);
        output.Cost.Should().Be(10.0m);
    }
}
