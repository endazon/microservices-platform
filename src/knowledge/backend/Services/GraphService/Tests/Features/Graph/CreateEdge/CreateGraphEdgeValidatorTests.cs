using AwesomeAssertions;
using GraphService.Features.Graph.CreateEdge;
using Knowledge.Contracts.Dtos;

namespace GraphService.Tests.Features.Graph.CreateEdge;

// FR-17, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395:
// 辺の作成の入力検証を手書きガード節から AbstractValidator へ移した際の**振る舞い同値**を固定する。
//
// 🔴 **固定するのは「落ちること」だけではない。** 移送前の応答本文（`{ "error": "..." }` の
// 文字列）まで同じであることを見る —— メッセージだけ変わる退行は状態コードでは捕まらない。
[Trait("TestKind", "Unit")]
public class CreateGraphEdgeValidatorTests
{
    private readonly CreateGraphEdgeValidator _validator = new();

    // 陽性対照: 両端が別の非空 Guid なら通る。
    // **これが無いと「常に落ちる検証器」でも陰性側のテストが全部緑になる。**
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(
            new CreateGraphEdgeRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 陽性対照 2: `EdgeTypeId` は検証対象ではない（空 Guid でも検証は通る）。
    // 型の実在は DB を引いた結果であり、端点が認可の後ろで `unknown_edge_type` を返す。
    [Fact]
    public void EmptyEdgeTypeId_Passes_BecauseItIsNotInputValidation()
    {
        var result = _validator.Validate(
            new CreateGraphEdgeRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty));

        result.IsValid.Should().BeTrue();
    }

    // 陰性 1: 起点が空 Guid。**移送前と同じ本文**になる。
    [Fact]
    public void EmptySourceDocumentId_FailsWithOriginalMessage()
    {
        var result = _validator.Validate(
            new CreateGraphEdgeRequest(Guid.Empty, Guid.NewGuid(), Guid.NewGuid()));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(CreateGraphEdgeValidator.DocumentIdRequiredMessage);
        CreateGraphEdgeValidator.DocumentIdRequiredMessage.Should().Be("document_id_required");
    }

    // 陰性 2: 終点が空 Guid。**片側だけ見ると「起点しか検証していない」実装が緑になる。**
    [Fact]
    public void EmptyTargetDocumentId_FailsWithOriginalMessage()
    {
        var result = _validator.Validate(
            new CreateGraphEdgeRequest(Guid.NewGuid(), Guid.Empty, Guid.NewGuid()));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(CreateGraphEdgeValidator.DocumentIdRequiredMessage);
    }

    // 陰性 3: 自己ループ。**移送前と同じ本文**になる。
    [Fact]
    public void SelfEdge_FailsWithOriginalMessage()
    {
        var id = Guid.NewGuid();

        var result = _validator.Validate(new CreateGraphEdgeRequest(id, id, Guid.NewGuid()));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(CreateGraphEdgeValidator.SelfEdgeNotAllowedMessage);
        CreateGraphEdgeValidator.SelfEdgeNotAllowedMessage.Should().Be("self_edge_not_allowed");
    }

    // 🔴 **規則の宣言順が応答の契約の一部である。** 端点は `Errors[0]` を本文へ載せるため、
    // 複数違反したときにどれが出るかは宣言順で決まる。
    //
    // **両端とも空 Guid のとき、自己ループの規則も同時に違反する**（空 == 空）。移送前のガード節は
    // 必須判定を先に見ていたので `document_id_required` が返る。順序を入れ替えたらここで止まる。
    [Fact]
    public void BothIdsEmpty_ReportsDocumentIdRequiredFirst()
    {
        var result = _validator.Validate(
            new CreateGraphEdgeRequest(Guid.Empty, Guid.Empty, Guid.NewGuid()));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2, "両端が空なら自己ループ規則も同時に違反する");
        result.Errors[0].ErrorMessage.Should().Be(CreateGraphEdgeValidator.DocumentIdRequiredMessage);
    }
}
