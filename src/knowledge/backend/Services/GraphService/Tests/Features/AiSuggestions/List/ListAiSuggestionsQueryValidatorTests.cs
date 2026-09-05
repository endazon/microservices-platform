using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Features.AiSuggestions;
using GraphService.Features.AiSuggestions.List;

namespace GraphService.Tests.Features.AiSuggestions.List;

// FR-18, SC-21, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395:
// AI 提案の一覧のクエリ引数の検証を手書きガード節から AbstractValidator へ移した際の
// **振る舞い同値**を固定する。
[Trait("TestKind", "Unit")]
public class ListAiSuggestionsQueryValidatorTests
{
    private readonly ListAiSuggestionsQueryValidator _validator = new();

    // 陽性対照: 未指定は通る（既定 pending へ縮退するのは端点側の仕事）。
    // **これが無いと「常に落ちる検証器」でも陰性側のテストが全部緑になる。**
    [Fact]
    public void UnspecifiedQuery_Passes()
    {
        var result = _validator.Validate(new ListAiSuggestionsQuery(null, null));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 陽性対照 2: 語彙の値と、絞りを外す語（`all`）は通る。
    // 🔴 **`all` は状態そのものではない** —— `SuggestionState.IsValid` は false を返すので、
    // その 1 項だけの述語に置き換えると `all` が 400 になる。
    [Theory]
    [InlineData(SuggestionState.Pending)]
    [InlineData(SuggestionState.Approved)]
    [InlineData(SuggestionState.Rejected)]
    [InlineData(AiSuggestionEndpoints.AnyState)]
    public void ValidState_Passes(string state)
    {
        var result = _validator.Validate(new ListAiSuggestionsQuery(state, null));

        result.IsValid.Should().BeTrue();
    }

    // 陰性 1: 語彙外の状態は落ち、**移送前と同じ本文**になる。
    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("PENDING")]
    public void InvalidState_FailsWithOriginalMessage(string state)
    {
        var result = _validator.Validate(new ListAiSuggestionsQuery(state, null));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(ListAiSuggestionsQueryValidator.InvalidStateMessage);
        ListAiSuggestionsQueryValidator.InvalidStateMessage.Should().Be("invalid_state");
    }

    // 陽性対照 3: 語彙の種別は通る。
    [Theory]
    [InlineData(SuggestionKind.Link)]
    [InlineData(SuggestionKind.Tag)]
    public void ValidKind_Passes(string kind)
    {
        var result = _validator.Validate(new ListAiSuggestionsQuery(null, kind));

        result.IsValid.Should().BeTrue();
    }

    // 陰性 2: 語彙外の種別は落ち、**移送前と同じ本文**になる。
    // 🔴 **`all` は種別には効かない**（状態の側だけの語である）。
    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(AiSuggestionEndpoints.AnyState)]
    public void InvalidKind_FailsWithOriginalMessage(string kind)
    {
        var result = _validator.Validate(new ListAiSuggestionsQuery(null, kind));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(ListAiSuggestionsQueryValidator.InvalidKindMessage);
        ListAiSuggestionsQueryValidator.InvalidKindMessage.Should().Be("invalid_kind");
    }

    // 🔴 **規則の宣言順が応答の契約の一部である。** 端点は `Errors[0]` を本文へ載せるため、
    // 両方違反したときにどれが出るかは宣言順で決まる。移送前は state を先に見ていた。
    [Fact]
    public void BothInvalid_ReportsStateFirst()
    {
        var result = _validator.Validate(new ListAiSuggestionsQuery("unknown", "unknown"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
        result.Errors[0].ErrorMessage.Should().Be(ListAiSuggestionsQueryValidator.InvalidStateMessage);
    }
}
