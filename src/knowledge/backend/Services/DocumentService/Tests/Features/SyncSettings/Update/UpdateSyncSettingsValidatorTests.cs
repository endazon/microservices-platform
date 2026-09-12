using AwesomeAssertions;
using DocumentService.Features.SyncSettings.Update;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.SyncSettings.Update;

// FR-20, UC-11, SC-20 主要素 3, ADR-0037 決定 3・4, #1442: 同期対象フォルダの入力検証。
//
// 🔴 **判定は正規化後の値に対して行う。** `/notes` と `notes/` を別物として通すと、
// 保存後に同じフォルダが 2 行できる（重複検査が素通りする）。
[Trait("TestKind", "Unit")]
public class UpdateSyncSettingsValidatorTests
{
    private readonly UpdateSyncSettingsValidator _validator = new();

    // 陽性対照: 空配列は正しい（＝全資料が対象。ADR-0037 決定 3 の既定）。
    [Fact]
    public void EmptyList_Passes()
    {
        var result = _validator.Validate(new UpdateSyncSettingsRequest([]));

        result.IsValid.Should().BeTrue("空＝全資料が対象であり、指定漏れではない");
    }

    // 陽性対照 2: 通常のフォルダ指定。
    [Fact]
    public void NormalFolders_Pass()
    {
        _validator.Validate(new UpdateSyncSettingsRequest(["work", "work/notes", "私用"]))
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("///")]
    public void EmptyElement_Fails(string folder)
    {
        var result = _validator.Validate(new UpdateSyncSettingsRequest([folder]));

        result.IsValid.Should().BeFalse();
        result.Errors[0].PropertyName.Should().Be(UpdateSyncSettingsValidator.TargetFoldersKey);
        UpdateSyncSettingsValidator.TargetFoldersKey.Should().Be("targetFolders");
        result.Errors[0].ErrorMessage.Should()
            .Be(UpdateSyncSettingsValidator.EmptyElementMessage);
    }

    // 🔴 **正規化してから重複を見る。** 見た目が違う 3 つは同じフォルダである。
    [Theory]
    [InlineData("work", "work")]
    [InlineData("work", "/work")]
    [InlineData("work", "work/")]
    public void DuplicateAfterNormalization_Fails(string a, string b)
    {
        var result = _validator.Validate(new UpdateSyncSettingsRequest([a, b]));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(UpdateSyncSettingsValidator.DuplicateMessage);
    }

    // 陰性対照（重複ではない）: 前方一致で重なるだけの 2 つは別のフォルダである。
    [Fact]
    public void SimilarButDifferentFolders_Pass()
    {
        _validator.Validate(new UpdateSyncSettingsRequest(["work", "work-old"]))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void TooLongFolder_Fails()
    {
        var result = _validator.Validate(new UpdateSyncSettingsRequest([new string('a', 1025)]));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(UpdateSyncSettingsValidator.TooLongMessage);
    }

    // 境界（対）: ちょうど 1024 文字は通る。
    [Fact]
    public void FolderAtTheLengthLimit_Passes()
    {
        _validator.Validate(new UpdateSyncSettingsRequest([new string('a', 1024)]))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void TooManyFolders_Fails()
    {
        var folders = Enumerable.Range(0, 101).Select(i => $"f{i}").ToList();

        var result = _validator.Validate(new UpdateSyncSettingsRequest(folders));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(UpdateSyncSettingsValidator.TooManyMessage);
    }

    // 境界（対）: ちょうど 100 件は通る。
    [Fact]
    public void OneHundredFolders_Pass()
    {
        var folders = Enumerable.Range(0, 100).Select(i => $"f{i}").ToList();

        _validator.Validate(new UpdateSyncSettingsRequest(folders)).IsValid.Should().BeTrue();
    }

    // 🔴 宣言順が応答の契約である（`ValidationProblems.FirstViolation` は先頭 1 件を返す）。
    // 空要素と件数超過が同時に起きたら、**空要素**が返る。
    [Fact]
    public void MultipleViolations_ReportTheFirstDeclaredRule()
    {
        var folders = Enumerable.Range(0, 101).Select(i => $"f{i}").ToList();
        folders[0] = "";

        var result = _validator.Validate(new UpdateSyncSettingsRequest(folders));

        result.Errors[0].ErrorMessage.Should().Be(UpdateSyncSettingsValidator.EmptyElementMessage);
    }
}
