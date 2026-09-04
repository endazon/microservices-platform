using AwesomeAssertions;
using ConversionService.Features.ConversionJobs.CorrectFigure;
using Knowledge.Contracts.Dtos;

namespace ConversionService.Tests.Features.ConversionJobs.CorrectFigure;

// UC-06, SC-07, IADR-0154 決定 3, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0377: 人手補正の入力検証を手書きガード節から AbstractValidator へ
// 移した際の**振る舞い同値**を固定する。
//
// 🔴 **固定するのは「落ちること」だけではない。** 移送前の応答本文（`{ "error": "..." }` の
// 文字列）まで同じであることを見る —— メッセージだけ変わる退行は状態コードでは捕まらない。
[Trait("TestKind", "Unit")]
public class FigureCorrectionValidatorTests
{
    private readonly FigureCorrectionValidator _validator = new();

    // 陽性対照: 正常な補正は通る。
    // **これが無いと「常に落ちる検証器」でも陰性側のテストが全部緑になる。**
    [Theory]
    [InlineData("mermaid", "flowchart TD; A-->B;")]
    [InlineData("plantuml", "@startuml\nA -> B\n@enduml")]
    public void ValidRequest_Passes(string language, string code)
    {
        var result = _validator.Validate(new FigureCorrectionRequest(language, code));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 陰性: 空・空白のみ・フェンス破り・言語の改行やバッククォートは落ち、
    // **移送前と同じ本文**になる。判定は Domain の `FigureMarkdown.IsEmbeddable` が持つ。
    [Theory]
    [InlineData("", "flowchart TD; A-->B;")]
    [InlineData("   ", "flowchart TD; A-->B;")]
    [InlineData("mermaid", "")]
    [InlineData("mermaid", "   ")]
    [InlineData("mer`maid", "flowchart TD; A-->B;")]
    [InlineData("mermaid\nplantuml", "flowchart TD; A-->B;")]
    [InlineData("mermaid", "flowchart TD;\n``" + "`\n<script>alert(1)</script>")]
    public void InvalidRequest_FailsWithOriginalMessage(string language, string code)
    {
        var result = _validator.Validate(new FigureCorrectionRequest(language, code));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(FigureCorrectionValidator.InvalidCorrectionMessage);
        FigureCorrectionValidator.InvalidCorrectionMessage.Should().Be("invalid_correction");
    }

    // 🔴 **規則は 1 本である。** 言語とコードを別々の規則に割ると違反件数が変わり、
    // `Errors[0]` を採る応答の意味も変わる。両方が不正でも報告は 1 件であることを固定する。
    [Fact]
    public void BothFieldsInvalid_ReportsSingleViolation()
    {
        var result = _validator.Validate(new FigureCorrectionRequest("", ""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
    }
}
