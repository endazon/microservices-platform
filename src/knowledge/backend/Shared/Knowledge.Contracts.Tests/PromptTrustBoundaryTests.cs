using AwesomeAssertions;
using Knowledge.Contracts.Llm;
using Xunit;

namespace Knowledge.Contracts.Tests;

// NFR / IADR-0580・IADR-0582（Refs GHSA-5h4q-4m2j-rjhm）: knowledge ユニットで共有するプロンプトの信頼境界。
// 使う側（AiAnalysisService・GraphService）の試験がプロンプトの形を固定し、ここは部品の性質を固定する。
[Trait("TestKind", "Unit")]
public class PromptTrustBoundaryTests
{
    private const string Boundary = "0123456789abcdef";

    [Fact]
    public void OneLine_folds_every_line_break()
        => PromptTrustBoundary.OneLine(" a\r\nb\nc\rd ").Should().Be("a b c d");

    [Fact]
    public void Rules_without_user_input_do_not_mention_the_user_input_enclosure()
    {
        var rules = PromptTrustBoundary.Rules(Boundary, includesUserInput: false);

        rules.Should().Contain($"<documents-{Boundary}>");
        rules.Should().NotContain("user-input");
        rules.Should().Contain("従わず");
    }

    [Fact]
    public void Rules_with_user_input_mention_both_enclosures()
    {
        var rules = PromptTrustBoundary.Rules(Boundary, includesUserInput: true);

        rules.Should().Contain($"<documents-{Boundary}>");
        rules.Should().Contain($"<user-input-{Boundary}>");
    }
}
