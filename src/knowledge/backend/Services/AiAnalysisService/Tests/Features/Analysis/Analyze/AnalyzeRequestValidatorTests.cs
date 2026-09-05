using AiAnalysisService.Domain;
using AiAnalysisService.Features.Analysis.Analyze;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;

namespace AiAnalysisService.Tests.Features.Analysis.Analyze;

// FR-07, UC-02, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0393:
// 分析依頼の入力検証を手書きガード節から AbstractValidator へ移した際の**振る舞い同値**を固定する。
//
// 🔴 **固定するのは「落ちること」だけではない。** 移送前の応答本文（`{ "error": "..." }` の
// 文字列）まで同じであることを見る —— メッセージだけ変わる退行は状態コードでは捕まらない。
[Trait("TestKind", "Unit")]
public class AnalyzeRequestValidatorTests
{
    private readonly AnalyzeRequestValidator _validator = new();

    // 陽性対照: 2 規則とも満たす依頼は通る。
    // **これが無いと「常に落ちる検証器」でも陰性側のテストが全部緑になる。**
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(
            new AnalysisTaskRequest("2025 年の経費規程を比較して", AnalysisTaskType.Compare));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 陽性対照 2: 範囲を省いた最小の依頼も通る（任意項目である）。
    [Fact]
    public void ValidRequest_WithoutRange_Passes()
    {
        var result = _validator.Validate(new AnalysisTaskRequest("要約して"));

        result.IsValid.Should().BeTrue();
    }

    // 陰性 1: 空・空白のみ・null は落ち、**移送前と同じ本文**になる。
    // 🔴 移送前の述語は `string.IsNullOrWhiteSpace` である。空白のみを通す実装
    // （`string.IsNullOrEmpty` や素の `NotEmpty()` への置き換え）はここで止まる。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData(null)]
    public void BlankInstruction_FailsWithOriginalMessage(string? instruction)
    {
        var result = _validator.Validate(new AnalysisTaskRequest(instruction!));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(AnalyzeRequestValidator.InstructionRequiredMessage);
        AnalyzeRequestValidator.InstructionRequiredMessage.Should().Be("instruction is required");
    }

    // 陰性 2: 上限超過の指示は落ち、**移送前と同じ本文**になる。
    [Fact]
    public void TooLongInstruction_FailsWithOriginalMessage()
    {
        var instruction = new string('あ', AnalysisPromptBuilder.MaxInstructionLength + 1);

        var result = _validator.Validate(new AnalysisTaskRequest(instruction));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(AnalyzeRequestValidator.InstructionTooLongMessage);
        AnalyzeRequestValidator.InstructionTooLongMessage.Should()
            .Be("instruction must be 2000 characters or fewer");
    }

    // 境界（陽性側）: ちょうど上限の長さは通る。**境界を片側だけ見ると off-by-one を見逃す。**
    [Fact]
    public void InstructionAtExactLimit_Passes()
    {
        var instruction = new string('あ', AnalysisPromptBuilder.MaxInstructionLength);

        var result = _validator.Validate(new AnalysisTaskRequest(instruction));

        result.IsValid.Should().BeTrue();
    }

    // 🔴 **規則の宣言順が応答の契約の一部である。** 端点は `Errors[0]` を本文へ載せるため、
    // 複数違反したときにどれが出るかは宣言順で決まる。移送前のガード節は必須を先に見ていた。
    // 順序を入れ替える変更が入ったらここで止まる。
    //
    // **空白のみ ＋ 上限超過**は両規則に触れる唯一の作り方である（空白は長さを持てる）。
    [Fact]
    public void MultipleViolations_ReportsRequiredFirst()
    {
        var instruction = new string(' ', AnalysisPromptBuilder.MaxInstructionLength + 1);

        var result = _validator.Validate(new AnalysisTaskRequest(instruction));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
        result.Errors[0].ErrorMessage.Should().Be(AnalyzeRequestValidator.InstructionRequiredMessage);
    }
}
