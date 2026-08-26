using RetailFlow.Domain.Common.ValueObjects;

namespace RetailFlow.UnitTests.Domain;

public class ProductCodeTests
{
    [Fact]
    public void Constructor_NormalizesToUppercaseAndTrims()
    {
        var code = new ProductCode("  sku-001  ");

        Assert.Equal("SKU-001", code.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Constructor_WithEmptyValue_Throws(string? value)
    {
        Assert.Throws<ArgumentException>(() => new ProductCode(value!));
    }

    [Fact]
    public void Constructor_WithTooLongValue_Throws()
    {
        var tooLong = new string('a', 65);

        Assert.Throws<ArgumentException>(() => new ProductCode(tooLong));
    }

    [Fact]
    public void Equality_IsCaseInsensitiveAfterNormalization()
    {
        Assert.Equal(new ProductCode("sku-001"), new ProductCode("SKU-001"));
    }
}
