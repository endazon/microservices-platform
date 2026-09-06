using AwesomeAssertions;
using DocumentService.Features.PrivateNotes.Purge;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.PrivateNotes.Purge;

// FR-19, UC-11, SC-19, ADR-0037 決定 20, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1・9: 完全削除の入力検証の**振る舞い同値**（#1278 PR-B）。
[Trait("TestKind", "Unit")]
public class PurgePrivateNotesValidatorTests
{
    private readonly PurgePrivateNotesValidator _validator = new();

    // 陽性対照（単票も一括も同じ端点。ids の要素数の差である）。
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(
            new PurgePrivateNotesRequest([Guid.NewGuid(), Guid.NewGuid()]));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 🔴 鍵とメッセージの両方を、**定数とリテラルの両方**へ当てる。
    // `OverridePropertyName` を消すと鍵が `Ids` になってここで止まる。
    [Fact]
    public void EmptyIds_FailsWithOriginalKeyAndMessage()
    {
        var result = _validator.Validate(new PurgePrivateNotesRequest([]));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].PropertyName.Should().Be(PurgePrivateNotesValidator.IdsKey);
        PurgePrivateNotesValidator.IdsKey.Should().Be("ids");
        result.Errors[0].ErrorMessage.Should().Be(PurgePrivateNotesValidator.IdsRequiredMessage);
        PurgePrivateNotesValidator.IdsRequiredMessage.Should()
            .Be("完全削除する資料の ID を 1 件以上指定してください。");
    }

    // 🔴 述語を写していること: **null も空リストと同じ 1 件**である
    // （移送前の `req.Ids is not { Count: > 0 }` は両者を区別しない）。
    [Fact]
    public void NullIds_FailsWithTheSameKeyAndMessage()
    {
        var result = _validator.Validate(new PurgePrivateNotesRequest(null!));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].PropertyName.Should().Be(PurgePrivateNotesValidator.IdsKey);
        result.Errors[0].ErrorMessage.Should().Be(PurgePrivateNotesValidator.IdsRequiredMessage);
    }
}
