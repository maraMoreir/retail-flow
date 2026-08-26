namespace RetailFlow.Domain.Common.ValueObjects;

/// <summary>
/// A product identifier (SKU-like) - shared kernel because Sales, Inventory, and
/// Fiscal all need to say "which product" without depending on each other's
/// Product/Sale/FiscalDocument entities. Normalized (trimmed, uppercased) so
/// "sku-001" and "SKU-001" are the same code, not silently different ones.
/// </summary>
public sealed class ProductCode : ValueObject
{
    private const int MaxLength = 64;

    public string Value { get; }

    public ProductCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Product code cannot be empty.", nameof(value));
        }

        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length > MaxLength)
        {
            throw new ArgumentException(
                $"Product code cannot exceed {MaxLength} characters.", nameof(value));
        }

        Value = normalized;
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}
