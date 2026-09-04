using AwesomeAssertions;
using FeedbackService.Domain;
using FeedbackService.Features.Feedback.Submit;
using Knowledge.Contracts.Dtos;

namespace FeedbackService.Tests.Features.Feedback.Submit;

// FR-08, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2:
// 投稿の入力検証を手書きガード節から AbstractValidator へ移した際の**振る舞い同値**を固定する。
//
// 🔴 **固定するのは「落ちること」だけではない。** 移送前の応答本文（`{ "error": "..." }` の
// 文字列）まで同じであることを見る —— メッセージだけ変わる退行は状態コードでは捕まらない。
[Trait("TestKind", "Unit")]
public class SubmitFeedbackValidatorTests
{
    private readonly SubmitFeedbackValidator _validator = new();

    // 陽性対照: 3 規則すべてを満たす要求は通る。
    // **これが無いと「常に落ちる検証器」でも陰性側のテストが全部緑になる。**
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = _validator.Validate(new FeedbackRequest(Guid.NewGuid(), "up", "良かった", "経費規程は？"));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // 陽性対照 2: 任意項目（Comment / Question）が null でも通る。
    [Fact]
    public void ValidRequest_WithoutOptionalFields_Passes()
    {
        var result = _validator.Validate(new FeedbackRequest(Guid.NewGuid(), "down"));

        result.IsValid.Should().BeTrue();
    }

    // 陽性対照 3: 大小の揺れ（"DOWN"）は不正ではない（正規化は端点側の Normalize が行う）。
    [Fact]
    public void ValidRequest_WithUppercaseRating_Passes()
    {
        var result = _validator.Validate(new FeedbackRequest(Guid.NewGuid(), "DOWN"));

        result.IsValid.Should().BeTrue();
    }

    // 陰性 1（T-05 と対）: 空 AnswerId は落ち、**移送前と同じ本文**になる。
    [Fact]
    public void EmptyAnswerId_FailsWithOriginalMessage()
    {
        var result = _validator.Validate(new FeedbackRequest(Guid.Empty, "up"));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(SubmitFeedbackValidator.AnswerIdRequiredMessage);
        SubmitFeedbackValidator.AnswerIdRequiredMessage.Should().Be("answerId is required");
    }

    // 陰性 2（T-04 と対）: up / down 以外は落ち、**移送前と同じ本文**になる。
    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    [InlineData("up ")]
    public void InvalidRating_FailsWithOriginalMessage(string rating)
    {
        var result = _validator.Validate(new FeedbackRequest(Guid.NewGuid(), rating));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(SubmitFeedbackValidator.RatingInvalidMessage);
        SubmitFeedbackValidator.RatingInvalidMessage.Should().Be("rating must be 'up' or 'down'");
    }

    // 陰性 3（T-06 と対）: 上限超過のコメントは落ち、**移送前と同じ本文**になる。
    [Fact]
    public void TooLongComment_FailsWithOriginalMessage()
    {
        var comment = new string('あ', AnswerFeedback.MaxCommentLength + 1);

        var result = _validator.Validate(new FeedbackRequest(Guid.NewGuid(), "up", comment));

        result.IsValid.Should().BeFalse();
        result.Errors[0].ErrorMessage.Should().Be(SubmitFeedbackValidator.CommentTooLongMessage);
        SubmitFeedbackValidator.CommentTooLongMessage.Should().Be("comment must be 2000 characters or fewer");
    }

    // 境界（陽性側）: ちょうど上限の長さは通る。**境界を片側だけ見ると off-by-one を見逃す。**
    [Fact]
    public void CommentAtExactLimit_Passes()
    {
        var comment = new string('あ', AnswerFeedback.MaxCommentLength);

        var result = _validator.Validate(new FeedbackRequest(Guid.NewGuid(), "up", comment));

        result.IsValid.Should().BeTrue();
    }

    // 🔴 **規則の宣言順が応答の契約の一部である。** 端点は `Errors[0]` を本文へ載せるため、
    // 複数違反したときにどれが出るかは宣言順で決まる。移送前のガード節は AnswerId を先に見ていた。
    // 順序を入れ替える変更が入ったらここで止まる。
    [Fact]
    public void MultipleViolations_ReportsAnswerIdFirst()
    {
        var result = _validator.Validate(new FeedbackRequest(
            Guid.Empty, "maybe", new string('あ', AnswerFeedback.MaxCommentLength + 1)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(3);
        result.Errors[0].ErrorMessage.Should().Be(SubmitFeedbackValidator.AnswerIdRequiredMessage);
    }
}
