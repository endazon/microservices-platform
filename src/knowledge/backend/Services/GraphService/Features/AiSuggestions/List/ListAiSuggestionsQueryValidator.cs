using FluentValidation;
using GraphService.Domain;

namespace GraphService.Features.AiSuggestions.List;

// FR-18, SC-21, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395:
// AI 提案の一覧のクエリ引数の入力規則。従前は Endpoint.cs 内の手書きガード節 2 本であった。
//
// 🔴 **振る舞いを変えない移送である。** 同じ 400・同じ本文（`{ "error": "..." }`）を返すため:
//   1. **規則の宣言順を元のガード節の順に揃える**（state → kind）。呼び出し側が `Errors[0]` を
//      採るので、順序を入れ替えると両方違反したときの本文が変わる。
//   2. **述語も元のまま写す。** `state` は「未指定」と `all`（絞りを外す語）を受理してから
//      語彙を見る 3 項の述語であり、`kind` は「未指定」を受理してから語彙を見る 2 項である。
//      **同じ形に見えるからと揃えない。**
//
// 🔴 **検証は認可より前に置くのが仕様である**（端点側の注記を参照）。ここは規則だけを持つ。
internal sealed class ListAiSuggestionsQueryValidator : AbstractValidator<ListAiSuggestionsQuery>
{
    // FR-18: 元のガード節が返していた本文の文字列。**この 2 本が応答の契約である。**
    internal const string InvalidStateMessage = "invalid_state";
    internal const string InvalidKindMessage = "invalid_kind";

    public ListAiSuggestionsQueryValidator()
    {
        // SC-21: 状態の絞り。未指定は既定（pending）へ、`all` は絞りを外す語であり状態ではない。
        RuleFor(q => q.State)
            .Must(s => s is null
                || s == AiSuggestionEndpoints.AnyState
                || SuggestionState.IsValid(s))
            .WithMessage(InvalidStateMessage);

        // SC-21: 種別の絞り（link / tag）。未指定は絞らない。
        RuleFor(q => q.Kind)
            .Must(k => k is null || SuggestionKind.IsValid(k))
            .WithMessage(InvalidKindMessage);
    }
}
