using AwesomeAssertions;
using DocumentService.Features.ObsidianSync.Move;

namespace DocumentService.Tests.Features.ObsidianSync.Move;

// FR-20, UC-11, SC-20, ADR-0037 決定 2・7, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1・9: リネームの入力検証の**振る舞い同値**（#1278 PR-B）。
//
// 🔴 固定するのは「落ちること」だけではない —— 移送前の応答の**鍵**と**メッセージ**、
// そして**宣言順**（どちらが先に出るか）まで見る。
[Trait("TestKind", "Unit")]
public class MoveNoteValidatorTests
{
    private readonly MoveNoteValidator _validator = new();

    // 陽性対照。**これが無いと「常に落ちる検証器」でも陰性側が全部緑になる。**
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(new MoveNoteRequest("notes/new.md", 3));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 🔴 述語を写していること: **`version = 0` は有効**である
    // （`NotEmpty()` へ置き換えると 0 が落ちて 400 に化ける）。
    [Fact]
    public void ZeroVersion_Passes()
    {
        _validator.Validate(new MoveNoteRequest("notes/new.md", 0)).IsValid.Should().BeTrue();
    }

    // 🔴 鍵とメッセージの両方を、**定数とリテラルの両方**へ当てる。
    // `OverridePropertyName` を消すと鍵が `VaultPath` になってここで止まる。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankVaultPath_FailsWithOriginalKeyAndMessage(string vaultPath)
    {
        var result = _validator.Validate(new MoveNoteRequest(vaultPath, 3));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(MoveNoteValidator.VaultPathKey);
        MoveNoteValidator.VaultPathKey.Should().Be("vaultPath");
        result.Errors[0].ErrorMessage.Should().Be(MoveNoteValidator.VaultPathRequiredMessage);
        MoveNoteValidator.VaultPathRequiredMessage.Should()
            .Be("移動先の vaultPath を指定してください。");
    }

    // `OverridePropertyName` を消すと鍵が `Version` になってここで止まる。
    [Fact]
    public void MissingVersion_FailsWithOriginalKeyAndMessage()
    {
        var result = _validator.Validate(new MoveNoteRequest("notes/new.md", null));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(MoveNoteValidator.VersionKey);
        MoveNoteValidator.VersionKey.Should().Be("version");
        result.Errors[0].ErrorMessage.Should().Be(MoveNoteValidator.VersionRequiredMessage);
        MoveNoteValidator.VersionRequiredMessage.Should()
            .Be("リネームには version（最後に見た版）が必須です。");
    }

    // 🔴 **宣言順が応答の契約である（O 軸）。** 移送前は `vaultPath` のガード節が先にあり、
    // **そこで `return` していた**ので、両方欠けた要求でも本文の 1 件は `vaultPath` である。
    // 端点は `FirstViolation` で `Errors[0]` を採るため、宣言順を入れ替えると応答が変わる。
    [Fact]
    public void BothMissing_ReportsVaultPathFirst()
    {
        var result = _validator.Validate(new MoveNoteRequest("  ", null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
        result.Errors[0].PropertyName.Should().Be(MoveNoteValidator.VaultPathKey);
        result.Errors[1].PropertyName.Should().Be(MoveNoteValidator.VersionKey);
    }
}
