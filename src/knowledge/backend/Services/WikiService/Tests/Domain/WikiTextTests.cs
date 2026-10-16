using System.Globalization;
using System.Text;
using AwesomeAssertions;
using WikiService.Domain;

namespace WikiService.Tests.Domain;

// FR-13 テスト仕様 T-32 (#1966) —— [[IADR-0554]] 決定 1・2: 切り詰めは符号位置で数え、書記素クラスタの境界でだけ切る。
[Trait("TestKind", "Unit")]
public class WikiTextTests
{
    private const int Max = 255;

    // 上限を超える入力（どれも 255 符号位置を超える）。素朴に string[..254] で切ると、どれも文字の途中で切れる。
    public static TheoryData<string, string> Inputs => new()
    {
        { "surrogate-pair", new string('a', 1) + string.Concat(Enumerable.Repeat("𠮷", 300)) },
        { "combining-mark", "a" + string.Concat(Enumerable.Repeat("が", 200)) },
        { "variation-selector", "a" + string.Concat(Enumerable.Repeat("葛\U000E0100", 200)) },
        { "zwj-emoji", "ab" + string.Concat(Enumerable.Repeat("👨‍👩‍👧", 80)) },
        { "flag-pair", "a" + string.Concat(Enumerable.Repeat("🇯🇵", 200)) },
    };

    [Theory]
    [MemberData(nameof(Inputs))]
    public void 切り詰めはサロゲートペアと結合文字の途中で切らない(string kind, string input)
    {
        _ = kind;
        var result = WikiText.FitColumn(input, Max, out var truncated);

        truncated.Should().BeTrue();
        WikiText.CountChars(result).Should().BeLessThanOrEqualTo(Max);
        result.Should().EndWith(WikiText.Ellipsis);
        var head = result[..^WikiText.Ellipsis.Length];
        input.Should().StartWith(head);
        // 切った位置は書記素クラスタの境界（次の要素の先頭）である。
        StringInfo.ParseCombiningCharacters(input).Should().Contain(head.Length);
        // 孤立したサロゲートを作らない（作ると UTF-8 へ符号化できない）。
        head.EnumerateRunes().Should().NotContain(Rune.ReplacementChar);
        // 境界まで詰める（1 要素分より多くは余らせない）。
        WikiText.CountChars(head).Should().BeGreaterThan(Max - 1 - 8);
    }

    [Fact]
    public void 符号位置で数える()
    {
        // BMP 外の文字 255 個は string.Length では 510 だが、PostgreSQL の varchar(255) には入る。
        var input = string.Concat(Enumerable.Repeat("𠮷", Max));
        input.Length.Should().Be(510);

        var result = WikiText.FitColumn(input, Max, out var truncated);

        truncated.Should().BeFalse();
        result.Should().BeSameAs(input);
    }

    [Fact]
    public void 印を付けない切り詰めは上限まで詰める()
    {
        var result = WikiText.FitColumn(new string('x', 600), 500, out var truncated, mark: false);

        truncated.Should().BeTrue();
        result.Should().Be(new string('x', 500));
    }
}
