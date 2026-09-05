using DocumentService.Domain;
using FluentValidation;

namespace DocumentService.Features.Documents.GrantShare;

// FR-20, ADR-0036 D-06, ADR-0061 決定 5, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0395 / [[IADR-0398]] 決定 1・9: 共有の付与の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **述語の粒度を写す。** 移送前は `subjectId` の空と `subjectType` の不正を **1 本の `||`** で見て、
// **1 つのメッセージ**を返していた。2 本の `RuleFor` に割ると、両方不正な要求で失敗が 2 件になり、
// メッセージも 2 つに割れる（`Errors[0]` を採るので本文の 1 件は変わらないが、**件数と規則の粒度が
// 変わる**。等価性の G 軸。IADR-0395 決定 8）。`BothInvalid_ReportsOneFailure` が固定する。
//
// 🔴 **鍵は `errors` である**（この 1 サイトだけ属性名ではない）。移送前がそう返していた ——
// メッセージが 2 つの項目にまたがるため、どちらか一方の名前を鍵にできない。
// 推論名は `SubjectId` になるので、`OverridePropertyName` で明示する。
internal sealed class GrantDocumentShareValidator : AbstractValidator<CreateShareRequest>
{
    // FR-20: 移送前のガード節が返していた鍵。
    internal const string ErrorsKey = "errors";

    // 🔴 `const` にできない（`string.Join` は定数式でない）。**移送前と同じ式から作る**ので、
    // `ShareSubjectType.All` が増減すれば本文も追随する（語彙を 2 箇所に持たない）。
    internal static readonly string SubjectInvalidMessage =
        $"subjectType は {string.Join(" / ", ShareSubjectType.All)} のいずれか、"
        + "subjectId は非空である必要があります。";

    public GrantDocumentShareValidator()
    {
        // 1 本の述語のまま写す（`||` を割らない）。述語も元のまま
        // （`IsNullOrWhiteSpace` を `NotEmpty()` へ置き換えない）。
        RuleFor(r => r.SubjectId)
            .Must((req, _) => !string.IsNullOrWhiteSpace(req.SubjectId)
                && ShareSubjectType.IsValid(req.SubjectType))
            .OverridePropertyName(ErrorsKey)
            .WithMessage(SubjectInvalidMessage);
    }
}
