using RetailFlow.Domain.Common.ValueObjects;

namespace RetailFlow.Domain.Sales;

/// <summary>
/// One priced line of a sale, as carried on <c>SaleCompletedEvent</c> - a payload
/// shape, not an entity (it has no identity of its own and isn't loaded/saved on
/// its own). The <c>Sale</c> aggregate that owns the authoritative version of this
/// data lands in Phase 3.
/// </summary>
public sealed record SaleLineItem(ProductCode ProductCode, int Quantity, Money UnitPrice);
