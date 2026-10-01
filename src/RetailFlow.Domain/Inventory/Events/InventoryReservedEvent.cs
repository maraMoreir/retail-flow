using RetailFlow.Domain.Common;

namespace RetailFlow.Domain.Inventory.Events;

/// <summary>
/// Raised once stock is held (not yet decremented - decrement happens on actual
/// fulfillment) for every line of a sale. The saga's success path from here moves
/// on to fiscal document generation.
/// </summary>
public sealed record InventoryReservedEvent(
    Guid SaleId,
    IReadOnlyList<InventoryReservationLine> Lines) : DomainEvent;
