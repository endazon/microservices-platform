using AwesomeAssertions;

namespace Platform.Shared.Kernel.Tests.Unit;

/// <summary>
/// NFR / IADR-0280 決定 6: DDD 基底型 <see cref="ValueObject"/> の等価性判定。
/// 構成要素の並びと値がすべて等しいときに等しい。
/// </summary>
public class ValueObjectTests
{
    private sealed class Money(string currency, decimal amount) : ValueObject
    {
        public string Currency { get; } = currency;
        public decimal Amount { get; } = amount;

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Currency;
            yield return Amount;
        }
    }

    private sealed class Weight(decimal amount) : ValueObject
    {
        public decimal Amount { get; } = amount;

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Amount;
        }
    }

    [Fact]
    public void 構成要素がすべて等しければ等しい()
    {
        var a = new Money("JPY", 100m);
        var b = new Money("JPY", 100m);

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void 構成要素が_1_つでも違えば等しくない()
    {
        var a = new Money("JPY", 100m);
        var b = new Money("USD", 100m);
        var c = new Money("JPY", 200m);

        a.Equals(b).Should().BeFalse();
        (a != c).Should().BeTrue();
    }

    [Fact]
    public void 型が違えば構成要素が同値でも等しくない()
    {
        var weight = new Weight(100m);
        var money = new Money("JPY", 100m);

        weight.Equals(money).Should().BeFalse();
    }

    [Fact]
    public void null_の構成要素も比較できる()
    {
        var a = new Money(null!, 0m);
        var b = new Money(null!, 0m);

        a.Equals(b).Should().BeTrue();
    }

    [Fact]
    public void null_とは等しくない()
    {
        var a = new Money("JPY", 100m);

        a.Equals(null).Should().BeFalse();
        (a == null).Should().BeFalse();
    }
}
