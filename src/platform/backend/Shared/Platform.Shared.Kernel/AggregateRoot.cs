namespace Platform.Shared.Kernel;

/// <summary>
/// 集約ルートの基底。ドメインイベントを蓄積し、永続化の単位になる。
/// </summary>
/// <typeparam name="TId">識別子の型。</typeparam>
/// <remarks>
/// NFR / IADR-0280 決定 6 / 計画 ADR-0030: 状態変化の事実は <see cref="Raise"/> で蓄積し、
/// 発行（Wolverine への引き渡し）は Infrastructure / Application 側が
/// <see cref="DomainEvents"/> を読み出して行う。読み出し後は <see cref="ClearDomainEvents"/> で
/// 空にする（二重発行を防ぐ）。発行の仕組み自体は本基底に持たせない —— Domain 層は
/// 外部ライブラリへ依存しない（計画 12_backend-application-stack §基本方針）。
/// </remarks>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>識別子を与えて生成する。</summary>
    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    /// <summary>ORM（EF Core）のマテリアライズ用。</summary>
    protected AggregateRoot()
    {
    }

    /// <summary>未発行のドメインイベント（発生順）。</summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>ドメインイベントを蓄積する。</summary>
    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    /// <summary>蓄積したドメインイベントを空にする（発行側が読み出した後に呼ぶ）。</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}
