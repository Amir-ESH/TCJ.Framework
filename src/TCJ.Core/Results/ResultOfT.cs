namespace TCJ.Core.Results;

/// <summary>
/// Represents the success or failure of an operation that returns a value.
/// </summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T value) : base(isSuccess: true, errors: [])
    {
        _value = value;
    }

    internal Result(IEnumerable<ResultError> errors) : base(isSuccess: false, errors: errors) { }

    /// <summary>
    /// Gets the successful value.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the result is a failure.
    /// </exception>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("A failed result does not contain a value.");

    /// <summary>
    /// Returns the successful value or the supplied fallback.
    /// </summary>
    public T? GetValueOrDefault(T? defaultValue = default)
        => IsSuccess ? _value : defaultValue;

    /// <summary>
    /// Transforms a successful value without changing failure information.
    /// </summary>
    public Result<TResult> Map<TResult>(Func<T, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);

        return IsSuccess ? Success(mapper(Value)) : Failure<TResult>(Errors);
    }

    /// <summary>
    /// Chains another result-returning operation after a success.
    /// </summary>
    public Result<TResult> Bind<TResult>(Func<T, Result<TResult>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);

        if (IsFailure)
        {
            return Failure<TResult>(Errors);
        }

        return binder(Value)
            ?? throw new InvalidOperationException("A result binder cannot return null.");
    }

    /// <summary>
    /// Converts a successful value to a failure when the predicate is not satisfied.
    /// </summary>
    public Result<T> Ensure(Func<T, bool> predicate, ResultError error)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(error);

        if (IsFailure || predicate(Value))
        {
            return this;
        }

        return Failure<T>(error);
    }

    /// <summary>
    /// Maps the result to one of two values.
    /// </summary>
    public TResult Match<TResult>(Func<T, TResult> onSuccess,
                                  Func<IReadOnlyList<ResultError>, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        return IsSuccess ? onSuccess(Value) : onFailure(Errors);
    }

    /// <summary>
    /// Executes one of two actions based on the result state.
    /// </summary>
    public void Switch(Action<T> onSuccess,
                       Action<IReadOnlyList<ResultError>> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        if (IsSuccess)
        {
            onSuccess(Value);
            return;
        }

        onFailure(Errors);
    }

    /// <summary>
    /// Executes a side effect only when the result succeeded.
    /// </summary>
    public Result<T> Tap(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsSuccess)
        {
            action(Value);
        }

        return this;
    }

    /// <summary>
    /// Executes a side effect only when the result failed.
    /// </summary>
    public new Result<T> TapFailure(Action<IReadOnlyList<ResultError>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsFailure)
        {
            action(Errors);
        }

        return this;
    }
}
