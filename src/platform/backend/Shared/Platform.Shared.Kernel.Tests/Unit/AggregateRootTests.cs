using AwesomeAssertions;

namespace Platform.Shared.Kernel.Tests.Unit;

/// <summary>
/// NFR / IADR-0280 決定 6: DDD 基底型 <see cref="AggregateRoot{TId}"/> のドメインイベント蓄積。
/// Raise で発生順に貯まり、ClearDomainEvents で空になる（読み出し後の二重発行を防ぐ）。
/// </summary>
public class AggregateRootTests
{
    private sealed record DocumentRenamed(string NewName) : IDomainEvent;

    private sealed record DocumentArchived : IDomainEvent;

    private sealed class Document(Guid id) : AggregateRoot<Guid>(id)
    {
        public void Rename(string newName) => Raise(new DocumentRenamed(newName));

        public void Archive() => Raise(new DocumentArchived());

        public void RaiseNull() => Raise(null!);
    }

    [Fact]
    public void 生成直後はドメインイベントが空である()
    {
        var doc = new Document(Guid.NewGuid());

        doc.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Raise_は発生順に蓄積する()
    {
        var doc = new Document(Guid.NewGuid());

        doc.Rename("新名称");
        doc.Archive();

        doc.DomainEvents.Should().HaveCount(2);
        doc.DomainEvents.First().Should().Be(new DocumentRenamed("新名称"));
        doc.DomainEvents.Last().Should().BeOfType<DocumentArchived>();
    }

    [Fact]
    public void ClearDomainEvents_で空になる()
    {
        var doc = new Document(Guid.NewGuid());
        doc.Rename("新名称");

        doc.ClearDomainEvents();

        doc.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void null_のイベントは拒む()
    {
        var doc = new Document(Guid.NewGuid());
        var act = () => doc.RaiseNull();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void 集約はエンティティとして識別子で等価判定される()
    {
        var id = Guid.NewGuid();
        var a = new Document(id);
        var b = new Document(id);
        b.Rename("イベントの有無は同一性に影響しない");

        a.Equals(b).Should().BeTrue();
    }
}
