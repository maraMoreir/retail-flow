using RetailFlow.Domain.Common;

namespace RetailFlow.Domain.Inventory.Events;

/// <summary>
/// Raised when a reservation is given back - either the sale was cancelled, or a
/// downstream saga step (fiscal authorization) failed and this is the
/// compensating action for the earlier <c>InventoryReservedEvent</c>.
/// </summary>
public sealed record InventoryReleasedEvent(Guid SaleId, string Reason) : DomainEvent;
