using AwesomeAssertions;
using FeedbackService.Domain;
using FeedbackService.Features.Feedback;

namespace FeedbackService.Tests.Features.Feedback;

// FR-08, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly）/ IADR-0371 決定 3:
// 手書きの詰め替えを生成マッパへ置き換えた際の**振る舞い同値**を固定する。
//
// 🔴 **生成物を信じるのではなく、写った値を見る。** Mapperly は名前が一致しないプロパティを
// 黙って落とすことがあり、**列が 1 つ抜けても型は通る**。8 プロパティを 1 つずつ見る。
[Trait("TestKind", "Unit")]
public class FeedbackMapperTests
{
    // 陽性: 全 8 プロパティが値を保ったまま写る。
    [Fact]
    public void ToDto_CopiesEveryProperty()
    {
        var answerId = Guid.NewGuid();
        var feedback = AnswerFeedback.Create(answerId, "alice", "down", "出典が不足していた", "経費規程は？");

        var dto = FeedbackMapper.ToDto(feedback);

        dto.Id.Should().Be(feedback.Id);
        dto.AnswerId.Should().Be(answerId);
        dto.Rating.Should().Be("down");
        dto.Comment.Should().Be("出典が不足していた");
        dto.Question.Should().Be("経費規程は？");
        dto.UserId.Should().Be("alice");
        dto.CreatedAt.Should().Be(feedback.CreatedAt);
        dto.UpdatedAt.Should().Be(feedback.UpdatedAt);
    }

    // 陰性: 任意項目の null は null のまま写る（空文字へ倒れない）。
    [Fact]
    public void ToDto_KeepsNullOptionalFields()
    {
        var feedback = AnswerFeedback.Create(Guid.NewGuid(), "bob", "up", null, null);

        var dto = FeedbackMapper.ToDto(feedback);

        dto.Comment.Should().BeNull();
        dto.Question.Should().BeNull();
    }

    // 陰性 2: 更新後は UpdatedAt が写り直る（写像が古い値を握らない）。
    [Fact]
    public void ToDto_ReflectsUpdatedState()
    {
        var feedback = AnswerFeedback.Create(Guid.NewGuid(), "carol", "up", null, null);
        var before = FeedbackMapper.ToDto(feedback);

        feedback.Update("down", "やっぱり不十分", "経費規程は？");
        var after = FeedbackMapper.ToDto(feedback);

        after.Rating.Should().Be("down");
        after.Comment.Should().Be("やっぱり不十分");
        after.CreatedAt.Should().Be(before.CreatedAt, "CreatedAt は更新で動かない");
        after.UpdatedAt.Should().BeOnOrAfter(before.UpdatedAt);
    }
}
