using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Features.EdgeTypes.Create;
using GraphService.Features.EdgeTypes.Rename;
using Knowledge.Contracts.Dtos;

namespace GraphService.Tests.Features.EdgeTypes;

// FR-17, SC-09, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395:
// 辺の型辞書（追加・改名）の入力検証を手書きガード節から AbstractValidator へ移した際の
// **振る舞い同値**を固定する。
//
// 🔴 **固定するのは「落ちること」だけではない。** 移送前の応答本文（`{ "error": "..." }` の
// 文字列）まで同じであることを見る —— メッセージだけ変わる退行は状態コードでは捕まらない。
[Trait("TestKind", "Unit")]
public class EdgeTypeValidatorTests
{
    private readonly CreateEdgeTypeValidator _create = new();
    private readonly RenameEdgeTypeValidator _rename = new();

    // 陽性対照: 名前と層が揃っていれば通る。
    // **これが無いと「常に落ちる検証器」でも陰性側のテストが全部緑になる。**
    [Fact]
    public void Create_ValidRequest_Passes()
    {
        var result = _create.Validate(new CreateEdgeTypeRequest("related", EdgeTypeLayer.Core, false));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 陰性 1: 空・空白のみの名前は落ちる（**正規化後**の空判定である）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_EmptyName_FailsWithOriginalMessage(string? name)
    {
        var result = _create.Validate(new CreateEdgeTypeRequest(name!, EdgeTypeLayer.Core, false));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(CreateEdgeTypeValidator.NameRequiredMessage);
        CreateEdgeTypeValidator.NameRequiredMessage.Should().Be("name_required");
    }

    // 陽性対照 2: 前後に空白があっても、正規化後に中身が残るなら通る。
    [Fact]
    public void Create_NameWithSurroundingWhitespace_Passes()
    {
        var result = _create.Validate(new CreateEdgeTypeRequest("  related  ", EdgeTypeLayer.Core, false));

        result.IsValid.Should().BeTrue();
    }

    // 陰性 2: 辞書外の層は落ちる。
    [Theory]
    [InlineData("misc")]
    [InlineData("")]
    [InlineData(null)]
    public void Create_InvalidLayer_FailsWithOriginalMessage(string? layer)
    {
        var result = _create.Validate(new CreateEdgeTypeRequest("related", layer!, false));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(CreateEdgeTypeValidator.InvalidLayerMessage);
        CreateEdgeTypeValidator.InvalidLayerMessage.Should().Be("invalid_layer");
    }

    // 🔴 **規則の宣言順が応答の契約の一部である。** 端点は `Errors[0]` を本文へ載せるため、
    // 両方違反したときにどれが出るかは宣言順で決まる。移送前は名前を先に見ていた。
    [Fact]
    public void Create_BothInvalid_ReportsNameFirst()
    {
        var result = _create.Validate(new CreateEdgeTypeRequest("  ", "misc", false));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
        result.Errors[0].ErrorMessage.Should().Be(CreateEdgeTypeValidator.NameRequiredMessage);
    }

    // 陽性対照 3（改名）: 中身のある名前は通る。
    [Fact]
    public void Rename_ValidRequest_Passes()
    {
        var result = _rename.Validate(new RenameEdgeTypeRequest("renamed"));

        result.IsValid.Should().BeTrue();
    }

    // 陰性 3（改名）: 空・空白のみの名前は落ちる。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rename_EmptyName_FailsWithOriginalMessage(string? name)
    {
        var result = _rename.Validate(new RenameEdgeTypeRequest(name!));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(RenameEdgeTypeValidator.NameRequiredMessage);
        RenameEdgeTypeValidator.NameRequiredMessage.Should().Be("name_required");
    }
}
