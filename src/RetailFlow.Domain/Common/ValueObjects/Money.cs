namespace RetailFlow.Domain.Common.ValueObjects;

/// <summary>
/// An amount tied to a currency - shared kernel, not owned by any one bounded
/// context: Sales prices a line item with it, Inventory costs stock with it,
/// Fiscal reports tax amounts with it. Negative amounts are rejected outright
/// (model a refund as a separate, explicit concept later - not as negative Money)
/// and arithmetic across different currencies throws rather than silently
/// producing a nonsense result.
/// </summary>
public sealed class Money : ValueObject
{
    public decimal Amount { get; }

    public Currency Currency { get; }

    public Money(decimal amount, Currency currency)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Money cannot be negative.");
        }

        Amount = decimal.Round(amount, 2, MidpointRounding.ToEven);
        Currency = currency;
    }

    public static Money Zero(Currency currency) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
        {
            throw new InvalidOperationException(
                $"Cannot combine {Currency} and {other.Currency} amounts.");
        }
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    public override string ToString() => $"{Amount:F2} {Currency}";
}
