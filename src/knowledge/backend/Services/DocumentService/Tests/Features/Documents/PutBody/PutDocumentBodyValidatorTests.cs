using AwesomeAssertions;
using DocumentService.Features.Documents.PutBody;

namespace DocumentService.Tests.Features.Documents.PutBody;

// FR-21, UC-03, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
// [[IADR-0398]] 決定 1・9: 本文投入の入力検証の**振る舞い同値**。
[Trait("TestKind", "Unit")]
public class PutDocumentBodyValidatorTests
{
    private readonly PutDocumentBodyValidator _validator = new();

    // 陽性対照。
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(new UpdateDocumentBodyRequest("# 本文"));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 🔴 **述語の粒度（G 軸）。空文字・空白は有効な本文である。**
    // `NotEmpty()` や `IsNullOrWhiteSpace` へ置き換えるとここで止まる（400 に化ける）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceBody_Passes(string body)
    {
        _validator.Validate(new UpdateDocumentBodyRequest(body)).IsValid.Should().BeTrue();
    }

    // 🔴 鍵とメッセージを、定数とリテラルの両方へ当てる。
    [Fact]
    public void NullBody_FailsWithOriginalKeyAndMessage()
    {
        var result = _validator.Validate(new UpdateDocumentBodyRequest(null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].PropertyName.Should().Be(PutDocumentBodyValidator.BodyKey);
        PutDocumentBodyValidator.BodyKey.Should().Be("body");
        result.Errors[0].ErrorMessage.Should().Be(PutDocumentBodyValidator.BodyRequiredMessage);
        PutDocumentBodyValidator.BodyRequiredMessage.Should().Be("本文は必須です。");
    }
}
