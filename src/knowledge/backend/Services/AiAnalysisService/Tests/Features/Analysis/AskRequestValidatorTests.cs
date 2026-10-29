using AiAnalysisService.Features.Analysis;
using AwesomeAssertions;

namespace AiAnalysisService.Tests.Features.Analysis;

// SC-01, FR-04 (#2321): 質問の入力規則と応答の本文。
[Trait("TestKind", "Unit")]
public class AskRequestValidatorTests
{
    private readonly AskRequestValidator _validator = new();

    [Fact]
    public void 上限は画面仕様書の暫定値1000文字()
        => AskRequestValidator.MaxQuestionLength.Should().Be(1000, "SPA の MAX_QUESTION_LENGTH と画面仕様書 §未決事項 3 に揃える");

    [Theory]
    [InlineData("")]
    [InlineData("  \t")]
    [InlineData(null)]
    public void 空の質問は必須の違反(string? question)
    {
        var result = _validator.Validate(new AskRequest(question!));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be("question is required");
    }

    [Fact]
    public void 上限を超える質問は上限の違反()
    {
        var result = _validator.Validate(new AskRequest(new string('a', AskRequestValidator.MaxQuestionLength + 1)));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be("question must be 1000 characters or fewer");
    }

    [Fact]
    public void 上限ちょうどは通る()
        => _validator.Validate(new AskRequest(new string('a', AskRequestValidator.MaxQuestionLength))).IsValid.Should().BeTrue();
}
