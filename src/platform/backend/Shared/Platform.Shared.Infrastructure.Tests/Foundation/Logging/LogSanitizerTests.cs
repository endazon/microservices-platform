using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Logging;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Logging;

// NFR, CodeQL(cs/log-forging) (#1019): 共有 sanitize の単体仕様。
//
// **陰性（消える）と陽性（残る）を必ず対にする** —— 「全部消す」実装でも陰性だけなら緑になる。
public class LogSanitizerTests
{
    // 陰性: 改行・復帰は行を割る。1 行に収まらなければ偽の監査行を注入できる。
    [Theory]
    [InlineData("tester\nAudit: action=config.write subject=root outcome=granted")]
    [InlineData("tester\r\n2026-08-30 INFO 偽の行")]
    [InlineData("tester\rINFO forged")]
    public void 改行と復帰は残らない(string forged)
    {
        var sanitized = LogSanitizer.Sanitize(forged);

        sanitized.Should().NotContain("\n").And.NotContain("\r");
    }

    // 陰性: 制御文字は種類を問わず落とす（char.IsControl の射程を固定する）。
    // **属性へ生の制御文字を書かない** —— ソースに紛れると読めず、整形器が壊す。
    // C0 制御域（0x00〜0x1F）と DEL・C1 制御域（0x7F〜0x9F）を走査で確かめる。
    [Fact]
    public void 制御文字はアンダースコアへ置き換わる()
    {
        foreach (var code in Enumerable.Range(0, 0xA0).Where(c => char.IsControl((char)c)))
        {
            var value = "a" + (char)code + "b";

            LogSanitizer.Sanitize(value).Should().Be(
                "a_b", $"U+{code:X4} は制御文字なので置換される");
        }
    }

    // 🔴 **除去ではなく置換である。** 消すと "a\nb" と "ab" が同じ行になり、
    // 注入を試みた痕跡が読めなくなる。この対照が無いと「消す」実装でも上が緑になる。
    [Fact]
    public void 制御文字は削除ではなく置換される_痕跡を残す()
    {
        LogSanitizer.Sanitize("a\nb").Should().Be("a_b").And.NotBe("ab");
    }

    // 陰性: 要求由来の値でログを溢れさせない。
    [Fact]
    public void 上限を超える値は切り詰められる()
    {
        var sanitized = LogSanitizer.Sanitize(new string('a', 5_000));

        sanitized.Length.Should().Be(LogSanitizer.DefaultMaxLength + 1, "切り詰め記号 … の 1 文字が付く");
        sanitized.Should().EndWith("…");
    }

    [Fact]
    public void 上限は引数で狭められる()
    {
        LogSanitizer.Sanitize("abcdefghij", maxLength: 4).Should().Be("abcd…");
    }

    // 🔴 陽性対照: **上限以内の通常の値はそのまま返る。**
    // これが無いと「常に空文字を返す」「常に切る」実装でも上の陰性がすべて緑になる。
    [Theory]
    [InlineData("config.read")]
    [InlineData("granted")]
    [InlineData("対象=pipeline.json")]
    [InlineData("user@example.com")]
    public void 通常の値は一文字も変わらない(string value)
    {
        LogSanitizer.Sanitize(value).Should().Be(value);
    }

    // 陽性対照: 上限ちょうどは切らない（境界の off-by-one を固定する）。
    [Fact]
    public void 上限ちょうどの長さは切り詰めない()
    {
        var exact = new string('a', LogSanitizer.DefaultMaxLength);

        LogSanitizer.Sanitize(exact).Should().Be(exact).And.NotEndWith("…");
    }

    // null / 空は空文字へ倒す（構造化ログでプロパティが欠落しないため）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void nullと空は空文字になる(string? value)
    {
        LogSanitizer.Sanitize(value).Should().Be(string.Empty);
    }
}
