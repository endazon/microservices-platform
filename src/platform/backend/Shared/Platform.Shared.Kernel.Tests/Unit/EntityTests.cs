using AwesomeAssertions;

namespace Platform.Shared.Kernel.Tests.Unit;

/// <summary>
/// NFR / IADR-0280 決定 6: DDD 基底型 <see cref="Entity{TId}"/> の同一性判定。
/// 同じ具象型かつ同じ識別子で等しく、<c>default</c> の識別子（未採番）は等しいと判定しない。
/// </summary>
public class EntityTests
{
    private sealed class Order(Guid id) : Entity<Guid>(id);

    private sealed class Invoice(Guid id) : Entity<Guid>(id);

    [Fact]
    public void 同じ型と識別子なら等しい()
    {
        var id = Guid.NewGuid();
        var a = new Order(id);
        var b = new Order(id);

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void 識別子が違えば等しくない()
    {
        var a = new Order(Guid.NewGuid());
        var b = new Order(Guid.NewGuid());

        a.Equals(b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void 型が違えば識別子が同じでも等しくない()
    {
        var id = Guid.NewGuid();
        var order = new Order(id);
        var invoice = new Invoice(id);

        order.Equals(invoice).Should().BeFalse();
    }

    [Fact]
    public void 未採番_default_の識別子どうしは等しいと判定しない()
    {
        // Result が default を成功として扱わないのと同じ判断: 「初期化していない」と「同じ」を
        // 同じ値にしない。
        var a = new Order(Guid.Empty);
        var b = new Order(Guid.Empty);

        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void 同一参照は未採番でも等しい()
    {
        var a = new Order(Guid.Empty);

        a.Equals(a).Should().BeTrue();
    }

    [Fact]
    public void null_とは等しくない()
    {
        var a = new Order(Guid.NewGuid());

        a.Equals(null).Should().BeFalse();
        (a == null).Should().BeFalse();
        ((Order?)null == null).Should().BeTrue();
    }
}
