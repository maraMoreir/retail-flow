using RetailFlow.Domain.Common;

namespace RetailFlow.Domain.Sales.Events;

/// <summary>Raised the moment a sale is opened - before any items are added or paid for.</summary>
public sealed record SaleCreatedEvent(Guid SaleId, Guid CustomerId) : DomainEvent;
