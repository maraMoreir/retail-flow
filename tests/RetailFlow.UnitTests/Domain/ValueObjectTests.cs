using RetailFlow.Domain.Common;

namespace RetailFlow.UnitTests.Domain;

public class ValueObjectTests
{
    private sealed class Money(decimal amount, string currency) : ValueObject
    {
        public decimal Amount { get; } = amount;
        public string Currency { get; } = currency;

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Amount;
            yield return Currency;
        }
    }

    [Fact]
    public void TwoValueObjects_WithEqualComponents_AreEqual()
    {
        var first = new Money(10m, "BRL");
        var second = new Money(10m, "BRL");

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void TwoValueObjects_WithDifferentComponents_AreNotEqual()
    {
        var tenReais = new Money(10m, "BRL");
        var tenDollars = new Money(10m, "USD");

        Assert.NotEqual(tenReais, tenDollars);
        Assert.True(tenReais != tenDollars);
    }
}
