using AwesomeAssertions;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// FR-11, ADR-0010, IADR-0528 (#1872): Anthropic 呼び出しの期限（HttpClient.Timeout）を設定
// `Llm:AnthropicTimeoutSeconds` から読むこと、**既定が 100 秒のまま**であること、不正値が既定へ倒れることを固定する。
// 既定が変わると、呼び出し側（期限を 100 秒より短く置いている）との内外関係が黙って崩れる。
[Trait("TestKind", "Unit")]
public class AnthropicHttpClientTests
{
    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(value is null
                ? []
                : new Dictionary<string, string?> { [AnthropicHttpClient.ConfigKey] = value })
            .Build();

    // T-33: 未設定なら 100 秒。従前（Timeout 未設定）の .NET 既定と同値であることも固定する（挙動不変）。
    [Fact]
    public void ResolveTimeout_WhenUnset_IsHundredSecondsSameAsHttpClientDefault()
    {
        var timeout = AnthropicHttpClient.ResolveTimeout(Config(null));

        timeout.Should().Be(TimeSpan.FromSeconds(100));
        AnthropicHttpClient.DefaultTimeoutSeconds.Should().Be(100);
        using var plain = new HttpClient();
        timeout.Should().Be(plain.Timeout);
    }

    // T-33: 設定キーの名前を固定する（運用文書・helm のコメントが同じ名前を書いている）。
    [Fact]
    public void ConfigKey_IsLlmAnthropicTimeoutSeconds()
    {
        AnthropicHttpClient.ConfigKey.Should().Be("Llm:AnthropicTimeoutSeconds");
    }

    // T-33: 正の整数は採る。warn は出さない。
    [Theory]
    [InlineData("180", 180)]
    [InlineData("1", 1)]
    [InlineData(" 240 ", 240)]
    [InlineData("2147483", 2147483)] // HttpClient が受け付ける上限ちょうど
    public void ResolveTimeout_WhenValid_UsesConfiguredSeconds(string value, int expectedSeconds)
    {
        using var loggers = new RecordingLoggerFactory();

        var timeout = AnthropicHttpClient.ResolveTimeout(Config(value), loggers.CreateLogger("t"));

        timeout.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
        loggers.Entries.Should().BeEmpty();
    }

    // T-33: 不正・0・負・数値でない・上限超は既定（100 秒）へ倒し、warn を 1 行出す（起動は止めない）。
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("100s")]
    [InlineData("2147484")] // HttpClient.Timeout の上限（int.MaxValue ミリ秒）を超える
    [InlineData("99999999999")]
    public void ResolveTimeout_WhenInvalid_FallsBackToDefaultWithWarning(string value)
    {
        using var loggers = new RecordingLoggerFactory();

        var timeout = AnthropicHttpClient.ResolveTimeout(Config(value), loggers.CreateLogger("t"));

        timeout.Should().Be(TimeSpan.FromSeconds(AnthropicHttpClient.DefaultTimeoutSeconds));
        loggers.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain(AnthropicHttpClient.ConfigKey).And.Contain(value);
    }

    // T-33: 空・空白は未設定と同じ扱い（黙って既定）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveTimeout_WhenBlank_FallsBackToDefaultSilently(string value)
    {
        using var loggers = new RecordingLoggerFactory();

        var timeout = AnthropicHttpClient.ResolveTimeout(Config(value), loggers.CreateLogger("t"));

        timeout.Should().Be(TimeSpan.FromSeconds(100));
        loggers.Entries.Should().BeEmpty();
    }

    // T-33: 生成した HttpClient の Timeout に設定値が載る（Program.cs が AnthropicClient へ渡すのはこれ）。
    [Theory]
    [InlineData(null, 100)]
    [InlineData("180", 180)]
    [InlineData("0", 100)]
    [InlineData("2147483", 2147483)]
    public void Create_AppliesResolvedTimeoutToHttpClient(string? value, int expectedSeconds)
    {
        using var client = AnthropicHttpClient.Create(
            Config(value), NullLogger<AnthropicResponseSanitizingHandler>.Instance);

        client.Timeout.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }
}
