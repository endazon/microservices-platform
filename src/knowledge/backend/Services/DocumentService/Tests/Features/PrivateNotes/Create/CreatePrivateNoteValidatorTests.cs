using AwesomeAssertions;
using DocumentService.Features.PrivateNotes.Create;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.PrivateNotes.Create;

// FR-19, UC-11, SC-19, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
// [[IADR-0398]] 決定 1・9: 個人資料の作成の入力検証の**振る舞い同値**（#1278 PR-B）。
[Trait("TestKind", "Unit")]
public class CreatePrivateNoteValidatorTests
{
    private readonly CreatePrivateNoteValidator _validator = new();

    // 陽性対照。
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(new CreatePrivateNoteRequest("メモ"));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // `vaultPath` は任意である（移送前も検証していない。端点が題名から作る）。
    [Fact]
    public void MissingVaultPath_Passes()
    {
        _validator.Validate(new CreatePrivateNoteRequest("メモ", null)).IsValid.Should().BeTrue();
    }

    // 🔴 鍵とメッセージの両方を、**定数とリテラルの両方**へ当てる。
    // `OverridePropertyName` を消すと鍵が `Title` になってここで止まる。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTitle_FailsWithOriginalKeyAndMessage(string title)
    {
        var result = _validator.Validate(new CreatePrivateNoteRequest(title));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].PropertyName.Should().Be(CreatePrivateNoteValidator.TitleKey);
        CreatePrivateNoteValidator.TitleKey.Should().Be("title");
        result.Errors[0].ErrorMessage.Should().Be(CreatePrivateNoteValidator.TitleRequiredMessage);
        CreatePrivateNoteValidator.TitleRequiredMessage.Should().Be("タイトルは必須です。");
    }
}
