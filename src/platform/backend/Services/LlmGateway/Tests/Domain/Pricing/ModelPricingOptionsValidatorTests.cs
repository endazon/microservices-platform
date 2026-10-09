using AwesomeAssertions;
using LlmGateway.Domain.Pricing;

namespace LlmGateway.Tests.Domain.Pricing;

// FR-10, NFR, ADR-0044 決定 3 (#443): 単価表の起動時検証。
// **区間の重なりを実行時に先勝ちで解決しない** —— どちらの単価で換算したかを後から特定できず、
// 費用の突合が成り立たなくなるためである。誤った単価表は配備の失敗として表に出す。
[Trait("TestKind", "Unit")]
public class ModelPricingOptionsValidatorTests
{
    private static readonly ModelPricingOptionsValidator Validator = new();

    private static ModelPricingOptions With(params ModelPriceEntry[] entries)
        => new() { Models = { ["m"] = [.. entries] } };

    private static ModelPriceEntry Entry(DateTimeOffset? from, DateTimeOffset? to, decimal price = 1m)
        => new()
        {
            EffectiveFrom = from,
            EffectiveTo = to,
            InputPerMillionTokens = price,
            OutputPerMillionTokens = price,
        };

    private static DateTimeOffset Day(int day) => new(2026, 9, day, 0, 0, 0, TimeSpan.Zero);

    // FR-10, ADR-0044 決定 3 (T-9): 隣接する区間（終了 = 次の開始）は**重ならない**。
    // 半開区間 [From, To) を採ったのはこの書き方を安全にするためである。
    [Fact]
    public void 隣接する区間は重なりとみなさない()
        => Validator.Validate(null, With(Entry(null, Day(1)), Entry(Day(1), null)))
            .Succeeded.Should().BeTrue();

    // FR-10, ADR-0044 決定 3 (T-10): 重なる区間は起動時に落とす。
    [Fact]
    public void 重なる区間は起動時に落とす()
    {
        var result = Validator.Validate(null, With(Entry(null, Day(5)), Entry(Day(1), null)));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("重なっています");
    }

    // FR-10, ADR-0044 決定 3 (T-11): 開始 >= 終了（空の区間）は落とす。
    // 空区間は「設定したのに一度も当たらない単価」であり、期間外警告として表に出るまで気付けない。
    [Fact]
    public void 空の有効期間は落とす()
        => Validator.Validate(null, With(Entry(Day(5), Day(1)))).Failed.Should().BeTrue();

    // FR-10, ADR-0044 決定 3 (T-12): 負の単価は落とす。
    [Fact]
    public void 負の単価は落とす()
        => Validator.Validate(null, With(Entry(null, null, -1m))).Failed.Should().BeTrue();

    // FR-10, ADR-0044 決定 3 (T-13): 単価が 1 件も無いモデル項目は落とす
    // （空の項目は「登録したつもり」が最も起きやすい形である）。
    [Fact]
    public void 空のモデル項目は落とす()
        => Validator.Validate(null, new ModelPricingOptions { Models = { ["m"] = [] } })
            .Failed.Should().BeTrue();

    // FR-10, ADR-0044 決定 3 (T-14): **検証器の陽性対照**。配備中の単価表と同じ形（期限なしの 1 区間を
    // 複数モデル分）の手書きの表が検証を通る。［2026-10-05 / #1741］従前は「実際に配備する appsettings」を
    // 検証すると書いていたが、ここは手書きの写しであり実設定を読まない。実設定の値は DeployedPriceTableTests が、
    // 実設定の形は起動時検証（ValidateOnStart）が守る。
    [Fact]
    public void 既定の単価表は検証を通る()
    {
        var options = new ModelPricingOptions
        {
            Models =
            {
                // ［2026-10-10 / #1875・IADR-0529］5.5 系の 3 モデル（haiku-5-5 はプロンプト長の 2 段）と、切り戻し用に残す旧モデル。
                ["claude-opus-5-5"] = [Entry(null, null, 4m)],
                ["claude-sonnet-5-5"] = [Entry(null, null, 2m)],
                ["claude-haiku-5-5"] = [Haiku55()],
                ["claude-opus-5"] = [Entry(null, null, 5m)],
                ["claude-sonnet-5"] = [Entry(null, null, 2m)], // #1741: 区切りなしの 1 区間
                ["claude-haiku-4-5"] = [Entry(null, null, 1m)],
            },
        };

        Validator.Validate(null, options).Succeeded.Should().BeTrue();
    }

    // FR-11, ADR-0044 決定 3, IADR-0529 (#1875): 配備中の haiku-5-5 と同じ形の 2 段の単価。
    private static ModelPriceEntry Haiku55(long threshold = 100_000, decimal longInput = 0.50m, decimal longOutput = 2.50m)
        => new()
        {
            InputPerMillionTokens = 0.10m,
            OutputPerMillionTokens = 0.50m,
            LongPrompt = new LongPromptPrice
            {
                ThresholdInputTokens = threshold,
                InputPerMillionTokens = longInput,
                OutputPerMillionTokens = longOutput,
            },
        };

    // FR-11, ADR-0044 決定 3, IADR-0529 (FR-10 T-43): 上段の境界が 1 未満だと**全要求が上段**になり静かに過大計上するので、起動時に落とす。
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void プロンプト長の上段の境界が1未満なら落とす(long threshold)
    {
        var result = Validator.Validate(null, With(Haiku55(threshold: threshold)));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ThresholdInputTokens");
    }

    // FR-11, ADR-0044 決定 3, IADR-0529 (FR-10 T-44): 上段の単価が負なら落とす（下段と同じ規則）。
    [Fact]
    public void プロンプト長の上段の単価が負なら落とす()
        => Validator.Validate(null, With(Haiku55(longOutput: -2.5m))).Failed.Should().BeTrue();

    // FR-11, ADR-0044 決定 3, IADR-0529 (FR-10 T-45): 境界 1（最小）は通る（陽性対照。境界の比較を > 0 に緩めても < 1 に締めても赤くなる対）。
    [Fact]
    public void プロンプト長の上段の境界1は通る()
        => Validator.Validate(null, With(Haiku55(threshold: 1))).Succeeded.Should().BeTrue();
}
