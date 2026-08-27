namespace Platform.Shared.Kernel;

/// <summary>
/// 識別子で同一性を判定するエンティティの基底。
/// </summary>
/// <typeparam name="TId">識別子の型（<see cref="Guid"/>・強い型付き ID 等）。</typeparam>
/// <remarks>
/// NFR / IADR-0280 決定 6 / 計画 ADR-0030: エンティティの同一性は属性ではなく識別子で決まる。
/// 等価性は**同じ具象型かつ同じ識別子**で判定し、<c>default</c> の識別子（未採番）は
/// 参照が同じ場合を除き**等しいと判定しない** —— 「初期化していない」と「同じ」が同じ値に
/// なると、未採番どうしの衝突が黙って同一視される（<see cref="Result"/> が <c>default</c> を
/// 成功として扱わないのと同じ判断である）。
/// </remarks>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    /// <summary>識別子を与えて生成する。</summary>
    protected Entity(TId id)
    {
        Id = id;
    }

    /// <summary>ORM（EF Core）のマテリアライズ用。識別子は永続層が設定する。</summary>
    protected Entity()
    {
        Id = default!;
    }

    /// <summary>識別子。</summary>
    public TId Id { get; protected set; }

    /// <summary>識別子が <c>default</c>（未採番）であるか。</summary>
    private bool IsTransient => EqualityComparer<TId>.Default.Equals(Id, default!);

    /// <inheritdoc />
    public bool Equals(Entity<TId>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (other.GetType() != GetType()) return false;
        if (IsTransient || other.IsTransient) return false;
        return EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Entity<TId> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    /// <summary>2 つのエンティティが同一か。</summary>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>2 つのエンティティが同一でないか。</summary>
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}
