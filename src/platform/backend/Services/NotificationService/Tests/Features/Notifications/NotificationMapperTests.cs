using AwesomeAssertions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;

namespace NotificationService.Tests.Features.Notifications;

// FR-22, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly）/ IADR-0371 決定 3 / IADR-0377:
// 手書きの詰め替えを生成マッパへ置き換えた際の**振る舞い同値**を固定する。
//
// 🔴 **生成物を信じるのではなく、写った値を見る。** Mapperly は名前が一致しないプロパティを
// 黙って落とすことがあり、**列が 1 つ抜けても型は通る**。7 プロパティを 1 つずつ見る。
[Trait("TestKind", "Unit")]
public class NotificationMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    // 陽性: 全 7 プロパティが値を保ったまま写る。
    [Fact]
    public void ToDto_CopiesEveryProperty()
    {
        var deadline = Now.AddDays(7);
        var notification = Notification.Create(
            "alice", NotificationKinds.PrivateNotePurgeImminent, Now,
            count: 3, thresholdPercent: 95, deadline: deadline);

        var dto = NotificationMapper.ToDto(notification);

        dto.Id.Should().Be(notification.Id);
        dto.Kind.Should().Be(NotificationKinds.PrivateNotePurgeImminent);
        dto.Count.Should().Be(3);
        dto.ThresholdPercent.Should().Be(95);
        dto.Deadline.Should().Be(deadline);
        dto.OccurredAt.Should().Be(Now);
        dto.Read.Should().BeFalse();
    }

    // 陰性: 任意項目の null は null のまま写る（0 へ倒れない）。
    // **`Count` が 0 になると「該当なし」と読めてしまう** —— null（この種別では意味を持たない）とは違う。
    [Fact]
    public void ToDto_KeepsNullOptionalFields()
    {
        var notification = Notification.Create("bob", NotificationKinds.StorageQuotaWarning, Now);

        var dto = NotificationMapper.ToDto(notification);

        dto.Count.Should().BeNull();
        dto.ThresholdPercent.Should().BeNull();
        dto.Deadline.Should().BeNull();
    }

    // 陰性 2: 既読化した状態が写り直る（写像が古い値を握らない）。
    [Fact]
    public void ToDto_ReflectsReadState()
    {
        var notification = Notification.Create("carol", NotificationKinds.SyncTokenExpiry, Now, count: 1);
        NotificationMapper.ToDto(notification).Read.Should().BeFalse();

        notification.MarkRead();

        NotificationMapper.ToDto(notification).Read.Should().BeTrue();
    }

    // 🔴 **宛先（`Subject`）は DTO に載らない。** 一覧は本人宛としてしか返らないため不要であり、
    // 生成マッパにも `[MapperIgnoreSource]` で明示してある。
    // **DTO 側に `Subject` を足せば属性が矛盾してビルドが止まる**が、この試験は
    // 「今の DTO に宛先の欄が無い」ことそのものを固定する。
    [Fact]
    public void Dto_HasNoSubjectMember()
    {
        typeof(NotificationDto).GetProperty("Subject").Should().BeNull(
            "宛先は応答に載せない（FR-22 / IADR-0215 決定 2）");
    }
}
