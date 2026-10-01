using RetailFlow.Shared.Results;

namespace RetailFlow.UnitTests.Shared;

public class ResultTests
{
    [Fact]
    public void Success_CarriesNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void Failure_CarriesTheGivenError()
    {
        var error = Error.NotFound("sale.not_found", "Sale not found.");

        var result = Result.Failure(error);

        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public void SuccessOfT_ExposesTheValue()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void FailureOfT_ThrowsWhenValueIsAccessed()
    {
        var result = Result.Failure<int>(Error.Validation("x", "invalid"));

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ImplicitConversion_FromValue_ProducesSuccess()
    {
        Result<int> result = 7;

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }
}
