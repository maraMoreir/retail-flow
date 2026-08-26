namespace RetailFlow.Domain.Common;

/// <summary>
/// Marker for a repository that loads/saves one aggregate root. Bounded contexts
/// define their own narrow interfaces (e.g. <c>ISaleRepository</c>) that extend
/// this rather than a generic CRUD surface - see docs/ADRs for the reasoning.
/// Implementations live in RetailFlow.Infrastructure.
/// </summary>
public interface IRepository<TAggregate, in TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    Task<TAggregate?> GetByIdAsync(TId id, CancellationToken cancellationToken = default);

    Task AddAsync(TAggregate aggregate, CancellationToken cancellationToken = default);
}
