using RetailFlow.Domain.Common.ValueObjects;

namespace RetailFlow.Domain.Inventory;

/// <summary>How much of one product a reservation holds - payload shape, not an entity.</summary>
public sealed record InventoryReservationLine(ProductCode ProductCode, int Quantity);
