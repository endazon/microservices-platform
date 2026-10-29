using FluentValidation;

namespace AiAnalysisService.Features.Analysis;

// SC-01, FR-04, UC-01, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 (#2321):
// 質問回答（`/ask`・`/ask/stream`）の入力規則。
//
// 計画 05_screens §SC-01 の入力表は質問を「必須・1文字以上、最大長制限」とし、値を定めていない。
// 値は画面仕様書の暫定値（1000 文字。§未決事項 3）と SPA の `MAX_QUESTION_LENGTH` に揃える。
// 従前は SPA だけが制限しており、SPA を通らない呼び出しは上限なしに RAG と LLM へ届いた。
//
// **`/ask` と `/ask/stream` の 2 操作が使う**ため 2 段目に置く（ADR-0068 決定 2。`AskRequest` と同じ）。
// 規則の宣言順（必須 → 上限長）と本文の形（`{ "error": "..." }`）は `AnalyzeRequestValidator` に揃える。
internal sealed class AskRequestValidator : AbstractValidator<AskRequest>
{
    /// <summary>質問の最大長（文字数）。画面仕様書の暫定値。計画が値を定めたらそれに従う。</summary>
    public const int MaxQuestionLength = 1000;

    internal const string QuestionRequiredMessage = "question is required";

    // `int` の補間は定数式にならない（CS0133）。数値を直書きせず、上限と同じ式から作る。
    internal static readonly string QuestionTooLongMessage =
        $"question must be {MaxQuestionLength} characters or fewer";

    public AskRequestValidator()
    {
        // 計画「1文字以上」。画面と同じく空白だけの質問も空とみなす。
        RuleFor(r => r.Question)
            .Must(question => !string.IsNullOrWhiteSpace(question))
            .WithMessage(QuestionRequiredMessage);

        // null 安全に書く（FluentValidation は全規則を走らせるので、ここへ null が来得る）。
        RuleFor(r => r.Question)
            .Must(question => question is not { Length: > MaxQuestionLength })
            .WithMessage(QuestionTooLongMessage);
    }

    // 2 つの端点が同じ形の 400 を返すための 1 か所。違反が無ければ null。
    internal static IResult? Reject(IValidator<AskRequest> validator, AskRequest req)
    {
        var result = validator.Validate(req);
        return result.IsValid ? null : Results.BadRequest(new { error = result.Errors[0].ErrorMessage });
    }
}
