using ConversionService.Domain;
using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace ConversionService.Features.ConversionJobs.CorrectFigure;

// UC-06, SC-07, IADR-0154 決定 3, 計画 ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0393: 人手補正の入力規則。
// 従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **振る舞いを変えない移送である。** 同じ 400・同じ本文（`{ "error": "invalid_correction" }`）を返す。
//
// **判定そのものは `FigureMarkdown.IsEmbeddable` が持ち、ここへ複写しない。**
// 空判定とコードフェンスの検出は Domain の知識であり、保存側（`TryReplaceImageWithCode`）と
// 同じ述語でなければならない。検証器へ写すと、片方だけ直したときに黙って割れる。
//
// **言語とコードの両方を見る 1 本の規則である**（`RuleFor(r => r)`）。片方ずつ 2 本に割ると、
// 違反の件数が変わり `Errors[0]` を採る応答の意味も変わる。
internal sealed class FigureCorrectionValidator : AbstractValidator<FigureCorrectionRequest>
{
    // UC-06: 元のガード節が返していた本文の文字列。**これが応答の契約である。**
    internal const string InvalidCorrectionMessage = "invalid_correction";

    public FigureCorrectionValidator()
    {
        RuleFor(r => r)
            .Must(r => FigureMarkdown.IsEmbeddable(r.Language, r.Code))
            .WithMessage(InvalidCorrectionMessage);
    }
}
