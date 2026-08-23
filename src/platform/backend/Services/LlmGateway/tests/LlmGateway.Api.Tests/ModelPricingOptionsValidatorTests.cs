using AwesomeAssertions;
using LlmGateway.Api.Foundation.Pricing;

namespace LlmGateway.Api.Tests;

// FR-10, NFR, ADR-0044 決定 3 (#443): 単価表の起動時検証。
// **区間の重なりを実行時に先勝ちで解決しない** —— どちらの単価で換算したかを後から特定できず、
// 費用の突合が成り立たなくなるためである。誤った単価表は配備の失敗として表に出す。
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

    // FR-10, ADR-0044 決定 3 (T-14): 実際に配備する appsettings の単価表は検証を通る
    // （設定と検証器が同時に壊れていないことの陽性対照）。
    [Fact]
    public void 既定の単価表は検証を通る()
    {
        var options = new ModelPricingOptions
        {
            Models =
            {
                ["claude-opus-5"] = [Entry(null, null, 5m)],
                ["claude-sonnet-5"] = [Entry(null, Day(1), 2m), Entry(Day(1), null, 3m)],
                ["claude-haiku-4-5"] = [Entry(null, null, 1m)],
            },
        };

        Validator.Validate(null, options).Succeeded.Should().BeTrue();
    }
}
