namespace RetailFlow.Domain.Common;

/// <summary>
/// Marker for anything raised by an aggregate as a fact that already happened.
/// Domain events are recorded in-memory by <see cref="AggregateRoot"/> and turned
/// into outbox messages by the infrastructure layer when the aggregate is saved -
/// the domain layer itself never publishes anything.
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredOnUtc { get; }
}

/// <summary>
/// Base record for domain events. Concrete events are immutable records that
/// describe a state change in the past tense (e.g. <c>SaleCompletedEvent</c>).
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();

    public DateTimeOffset OccurredOnUtc { get; } = DateTimeOffset.UtcNow;
}
