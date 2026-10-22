using SkyrimModPacker.Core.Common;
using Xunit;

namespace SkyrimModPacker.Tests;

public class ResultTests
{
    [Fact]
    public void Success_CreatesSuccessfulResult()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void Failure_CreatesFailedResult_WithErrorMessage()
    {
        var result = Result.Failure("Something went wrong");

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Equal("Something went wrong", result.Error);
    }

    [Fact]
    public void GenericSuccess_ContainsValue()
    {
        var result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void GenericFailure_ThrowsWhenAccessingValue()
    {
        var result = Result<int>.Failure("Error occurred");

        Assert.False(result.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => _ = result.Value);
    }

    [Fact]
    public void GenericImplicitConversion_WrapsValue()
    {
        Result<string> result = "test value";

        Assert.True(result.IsSuccess);
        Assert.Equal("test value", result.Value);
    }
}
