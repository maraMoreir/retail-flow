using RetailFlow.Domain.Common;
using RetailFlow.Domain.Common.ValueObjects;

namespace RetailFlow.Domain.Sales.Events;

/// <summary>
/// Raised once payment is confirmed and the sale is final. This is what the
/// CreateSale saga (see docs/ADRs/001-saga-pattern-for-transactions.md) uses to
/// kick off inventory reservation, fiscal document generation, and customer
/// notification - and what RetailFlow.Reporting projects into read-model KPIs.
/// </summary>
public sealed record SaleCompletedEvent(
    Guid SaleId,
    Money TotalAmount,
    IReadOnlyList<SaleLineItem> Items) : DomainEvent;
