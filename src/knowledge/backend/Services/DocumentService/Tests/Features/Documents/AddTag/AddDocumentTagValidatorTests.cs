using AwesomeAssertions;
using DocumentService.Features.Documents.AddTag;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.Documents.AddTag;

// FR-18, SC-03, SC-09, ADR-0063, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1・9: タグ付与の入力検証の**振る舞い同値**。
[Trait("TestKind", "Unit")]
public class AddDocumentTagValidatorTests
{
    private readonly AddDocumentTagValidator _validator = new();

    // 陽性対照（前後の空白は `Tag.Normalize` が落とすので有効）。
    [Theory]
    [InlineData("契約")]
    [InlineData("  契約  ")]
    public void ValidRequest_Passes(string name)
    {
        var result = _validator.Validate(new AddDocumentTagRequest(name));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 🔴 **述語は `Tag.Normalize` の後の `IsNullOrEmpty` である。**
    // `NotEmpty()` に置き換えると `"   "`（空白のみ）が通ってここで止まる。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void EmptyName_FailsWithOriginalKeyAndMessage(string name)
    {
        var result = _validator.Validate(new AddDocumentTagRequest(name));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].PropertyName.Should().Be(AddDocumentTagValidator.NameKey);
        AddDocumentTagValidator.NameKey.Should().Be("name");
        result.Errors[0].ErrorMessage.Should().Be(AddDocumentTagValidator.NameRequiredMessage);
        AddDocumentTagValidator.NameRequiredMessage.Should().Be("タグ名は必須です。");
    }

    // 移送前は `req.Name ?? string.Empty` を正規化していた。null も 400 である。
    [Fact]
    public void NullName_FailsWithOriginalKeyAndMessage()
    {
        var result = _validator.Validate(new AddDocumentTagRequest(null!));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(AddDocumentTagValidator.NameKey);
        result.Errors[0].ErrorMessage.Should().Be(AddDocumentTagValidator.NameRequiredMessage);
    }
}
