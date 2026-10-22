namespace SkyrimModPacker.Core.Common;

/// <summary>
/// Represents the outcome of an operation without a return value.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string Error { get; }

    protected Result(bool isSuccess, string error)
    {
        if (isSuccess && !string.IsNullOrEmpty(error))
            throw new InvalidOperationException("Successful result cannot have an error message.");

        if (!isSuccess && string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException("Failed result must have an error message.");

        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, string.Empty);
    public static Result Failure(string error) => new(false, error);
}

/// <summary>
/// Represents the outcome of an operation with a typed return value.
/// </summary>
/// <typeparam name="T">The type of the returned value.</typeparam>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value) : base(true, string.Empty)
    {
        _value = value;
    }

    private Result(string error) : base(false, error)
    {
        _value = default;
    }

    /// <summary>
    /// Gets the payload value if the operation succeeded; otherwise throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot access Value on a failed result: {Error}");

    public static Result<T> Success(T value) => new(value);
    public static new Result<T> Failure(string error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);
}
