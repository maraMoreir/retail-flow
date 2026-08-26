using RetailFlow.Domain.Common;

namespace RetailFlow.Domain.Sales.Events;

/// <summary>
/// Raised when a sale is cancelled - either by the customer/operator directly, or
/// as a saga compensating action after a downstream step (inventory reservation,
/// fiscal authorization) fails. <paramref name="Reason"/> distinguishes the two
/// for reporting/audit.
/// </summary>
public sealed record SaleCancelledEvent(Guid SaleId, string Reason) : DomainEvent;
