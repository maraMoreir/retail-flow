namespace RetailFlow.Domain.Common;

/// <summary>
/// An <see cref="Entity{TId}"/> that is the single entry point for a transactional
/// consistency boundary (e.g. <c>Sale</c>). Only aggregate roots have repositories;
/// everything reachable only through one (e.g. <c>SaleItem</c>) is loaded/saved as
/// part of it.
///
/// Domain events raised here are picked up by <c>UnitOfWork</c> in the
/// infrastructure layer and written to the outbox in the same transaction that
/// persists the aggregate - see docs/ADRs/002-outbox-pattern-for-reliability.md.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected AggregateRoot(TId id) : base(id)
    {
    }

    protected AggregateRoot()
    {
    }

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
