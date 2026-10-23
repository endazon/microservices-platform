using AwesomeAssertions;
using DocumentService.Domain;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Domain;

// FR-19, 計画 ADR-0036 D-08, [[IADR-0610]] (#2046): 発動記録の時間の規則（15 分・延長なし・本人の早期終了は早いほう）。
public class BreakGlassGrantTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private static BreakGlassGrant NewGrant() => BreakGlassGrant.Activate(Guid.NewGuid(), "alice", "admin", "理由", T0);

    [Fact]
    public void 有効時間は発動から15分で境界ちょうどは無効()
    {
        var grant = NewGrant();

        BreakGlassPolicy.ValidityMinutes.Should().Be(15, "ADR-0036 D-08 の値");
        grant.ExpiresAt.Should().Be(T0.AddMinutes(15));
        grant.IsActiveAt(T0).Should().BeTrue();
        grant.IsActiveAt(T0.AddMinutes(15).AddTicks(-1)).Should().BeTrue();
        grant.IsActiveAt(T0.AddMinutes(15)).Should().BeFalse();
        grant.IsActiveAt(T0.AddTicks(-1)).Should().BeFalse("発動より前は無効");
    }

    [Fact]
    public void 本人の終了は終了日時を早め_失効後の終了は効かない()
    {
        var grant = NewGrant();
        grant.EndByOwner(T0.AddMinutes(3)).Should().BeTrue();
        grant.EndsAt.Should().Be(T0.AddMinutes(3));
        grant.IsActiveAt(T0.AddMinutes(3)).Should().BeFalse();
        grant.EndByOwner(T0.AddMinutes(4)).Should().BeFalse("終えたものは重ねて終えない");

        var lapsed = NewGrant();
        lapsed.EndByOwner(T0.AddMinutes(20)).Should().BeFalse();
        lapsed.EndedAt.Should().BeNull();
        lapsed.EndsAt.Should().Be(T0.AddMinutes(15), "失効後に終了日時を後ろへずらさない");
    }

    [Theory]
    [InlineData("alice", "alice", "理由")]
    [InlineData("alice", "", "理由")]
    [InlineData("alice", "admin", " ")]
    [InlineData("", "admin", "理由")]
    public void 不変条件を破る発動は例外(string owner, string nominee, string reason)
        => FluentActions.Invoking(() => BreakGlassGrant.Activate(Guid.NewGuid(), owner, nominee, reason, T0))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void 監査ログの行は発動記録の6項目を写し本文の列を持たない()
    {
        var grant = NewGrant();
        var entry = BreakGlassAuditEntry.Viewed(grant, BreakGlassViewTargets.Content, T0.AddMinutes(1));

        entry.Should().BeEquivalentTo(new
        {
            GrantId = grant.Id,
            Operation = BreakGlassOperations.View,
            ActorId = "admin",
            OwnerId = "alice",
            DocumentId = grant.DocumentId,
            Reason = "理由",
            ActivatedAt = T0,
            EndsAt = T0.AddMinutes(15),
            ViewTarget = BreakGlassViewTargets.Content,
        });
        typeof(BreakGlassAuditEntry).GetProperties().Select(p => p.Name)
            .Should().NotContain(["Title", "Body", "Markdown", "Content"]);
    }
}
