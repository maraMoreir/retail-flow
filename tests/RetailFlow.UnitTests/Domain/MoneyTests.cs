using RetailFlow.Domain.Common.ValueObjects;

namespace RetailFlow.UnitTests.Domain;

public class MoneyTests
{
    [Fact]
    public void Constructor_RoundsToTwoDecimalPlaces()
    {
        var money = new Money(10.005m, Currency.BRL);

        // Banker's rounding (ToEven): 10.005 -> 10.00, not 10.01.
        Assert.Equal(10.00m, money.Amount);
    }

    [Fact]
    public void Constructor_WithNegativeAmount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(-1m, Currency.BRL));
    }

    [Fact]
    public void Add_WithSameCurrency_SumsAmounts()
    {
        var five = new Money(5m, Currency.BRL);
        var ten = new Money(10m, Currency.BRL);

        var result = five + ten;

        Assert.Equal(new Money(15m, Currency.BRL), result);
    }

    [Fact]
    public void Add_WithDifferentCurrencies_Throws()
    {
        var reais = new Money(5m, Currency.BRL);
        var dollars = new Money(5m, Currency.USD);

        Assert.Throws<InvalidOperationException>(() => reais + dollars);
    }

    [Fact]
    public void Subtract_WithSameCurrency_SubtractsAmounts()
    {
        var ten = new Money(10m, Currency.BRL);
        var three = new Money(3m, Currency.BRL);

        var result = ten - three;

        Assert.Equal(new Money(7m, Currency.BRL), result);
    }

    [Fact]
    public void Equality_IsByAmountAndCurrency()
    {
        Assert.Equal(new Money(10m, Currency.BRL), new Money(10m, Currency.BRL));
        Assert.NotEqual(new Money(10m, Currency.BRL), new Money(10m, Currency.USD));
    }

    [Fact]
    public void Zero_ProducesAZeroAmountInTheGivenCurrency()
    {
        var zero = Money.Zero(Currency.BRL);

        Assert.Equal(0m, zero.Amount);
        Assert.Equal(Currency.BRL, zero.Currency);
    }
}
