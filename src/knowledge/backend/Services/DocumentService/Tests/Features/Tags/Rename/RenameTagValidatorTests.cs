using AwesomeAssertions;
using DocumentService.Features.Tags.Rename;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.Tags.Rename;

// FR-09, SC-09, #635, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
// [[IADR-0398]] 決定 1・9: タグ辞書の改名の入力検証の**振る舞い同値**。
[Trait("TestKind", "Unit")]
public class RenameTagValidatorTests
{
    private readonly RenameTagValidator _validator = new();

    // 陽性対照。
    [Theory]
    [InlineData("契約")]
    [InlineData("  契約  ")]
    public void ValidRequest_Passes(string name)
    {
        var result = _validator.Validate(new RenameTagRequest(name));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 🔴 述語は `Tag.Normalize` の後の `IsNullOrEmpty`（`NotEmpty()` へ置き換えない）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyName_FailsWithOriginalKeyAndMessage(string name)
    {
        var result = _validator.Validate(new RenameTagRequest(name));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].PropertyName.Should().Be(RenameTagValidator.NameKey);
        RenameTagValidator.NameKey.Should().Be("name");
        result.Errors[0].ErrorMessage.Should().Be(RenameTagValidator.NameRequiredMessage);
        RenameTagValidator.NameRequiredMessage.Should().Be("タグ名は必須です。");
    }
}
