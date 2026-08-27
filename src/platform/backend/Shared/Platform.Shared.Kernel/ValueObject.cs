namespace Platform.Shared.Kernel;

/// <summary>
/// 構成要素の値で等価性を判定する値オブジェクトの基底。
/// </summary>
/// <remarks>
/// NFR / IADR-0280 決定 6 / 計画 ADR-0030: 値オブジェクトは識別子を持たず、
/// <see cref="GetEqualityComponents"/> が返す構成要素の**並びと値**がすべて等しいときに等しい。
/// 派生型は不変（イミュータブル）に設計すること。単純な値の組は C# の <c>record</c> でも
/// 表せる —— 本基底を使うのは、正規化や検証を伴う値に共通の等価性の器が要る場合である
/// （過剰な共通化を避ける。計画の構成図注記）。
/// </remarks>
public abstract class ValueObject : IEquatable<ValueObject>
{
    /// <summary>等価性の判定に使う構成要素を、宣言順に返す。</summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    /// <inheritdoc />
    public bool Equals(ValueObject? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (other.GetType() != GetType()) return false;
        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueObject other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GetType());
        foreach (var component in GetEqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    /// <summary>2 つの値オブジェクトが等しいか。</summary>
    public static bool operator ==(ValueObject? left, ValueObject? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>2 つの値オブジェクトが等しくないか。</summary>
    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
