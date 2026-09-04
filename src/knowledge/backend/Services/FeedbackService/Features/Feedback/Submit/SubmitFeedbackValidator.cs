using FeedbackService.Domain;
using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace FeedbackService.Features.Feedback.Submit;

// FR-08, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2:
// 投稿の入力規則。従前は Endpoint.cs 内の手書きガード節 3 本であった。
//
// 🔴 **振る舞いを変えない移送である。** 同じ 400・同じ本文（`{ "error": "..." }`）を返すため、
// 次の 2 点を守る:
//   1. **規則の宣言順を元のガード節の順に揃える**（AnswerId → Rating → Comment）。
//      FluentValidation は既定で全規則を走らせるが、呼び出し側が `Errors[0]` を採ることで
//      元の「最初の違反で返す」と同じ文字列になる。順序を入れ替えると本文が変わる。
//   2. **メッセージは元のリテラルをそのまま持ち上げる。** 定数にしたのは、テストが同じ文字列を
//      二度書いて片方だけ直す事故（文言だけ変わって誰も気づかない）を塞ぐためである。
internal sealed class SubmitFeedbackValidator : AbstractValidator<FeedbackRequest>
{
    // FR-08: 元のガード節が返していた本文の文字列。**この 3 本が応答の契約である。**
    internal const string AnswerIdRequiredMessage = "answerId is required";
    internal const string RatingInvalidMessage = "rating must be 'up' or 'down'";
    // 🔴 `const` にできない（`int` の補間は定数式にならない。CS0133）。移送前と同じ文字列を
    // **同じ式から**作るために `static readonly` にする。数値を直書きすると、カラム長を変えたときに
    // メッセージだけが古いまま残る。
    internal static readonly string CommentTooLongMessage =
        $"comment must be {AnswerFeedback.MaxCommentLength} characters or fewer";

    public SubmitFeedbackValidator()
    {
        // FR-08: 対象 AI 回答の ID。空 Guid は不可。
        RuleFor(r => r.AnswerId)
            .NotEqual(Guid.Empty)
            .WithMessage(AnswerIdRequiredMessage);

        // FR-08: 評価値は up / down のいずれか（大小の揺れは Normalize が吸収するため IsValid で見る）。
        RuleFor(r => r.Rating)
            .Must(FeedbackRating.IsValid)
            .WithMessage(RatingInvalidMessage);

        // FR-08: 自由記述の上限。null は許す（任意項目）。カラム長と同じ定数を使う。
        RuleFor(r => r.Comment)
            .Must(c => c is not { Length: > AnswerFeedback.MaxCommentLength })
            .WithMessage(CommentTooLongMessage);
    }
}
