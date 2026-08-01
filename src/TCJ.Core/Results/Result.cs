namespace TCJ.Core.Results;

/// <summary>
/// Represents the success or failure of an operation that does not return a value.
/// </summary>
public class Result
{
    private readonly IReadOnlyList<ResultError> _errors;

    /// <summary>
    /// Initializes a result while enforcing its success/failure invariants.
    /// </summary>
    protected Result(bool isSuccess, IEnumerable<ResultError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        ResultError[] errorArray = errors.ToArray();

        if (errorArray.Any(static error => error is null))
        {
            throw new ArgumentException("A result cannot contain a null error.", nameof(errors));
        }

        if (isSuccess && errorArray.Length > 0)
        {
            throw new ArgumentException("A successful result cannot contain errors.", nameof(errors));
        }

        if (!isSuccess && errorArray.Length == 0)
        {
            throw new ArgumentException("A failed result must contain at least one error.", nameof(errors));
        }

        IsSuccess = isSuccess;
        _errors = Array.AsReadOnly(errorArray);
    }

    /// <summary>
    /// Gets whether the operation succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets whether the operation failed.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Gets the failure reasons. Successful results expose an empty collection.
    /// </summary>
    public IReadOnlyList<ResultError> Errors => _errors;

    /// <summary>
    /// Gets the first failure reason, or <see langword="null"/> for success.
    /// </summary>
    public ResultError? FirstError
        => _errors.Count == 0 ? null : _errors[0];

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static Result Success()
        => new(isSuccess: true, errors: []);

    /// <summary>
    /// Creates a failed result containing one error.
    /// </summary>
    public static Result Failure(ResultError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(isSuccess: false, errors: [error]);
    }

    /// <summary>
    /// Creates a failed result containing one or more errors.
    /// </summary>
    public static Result Failure(IEnumerable<ResultError> errors)
        => new(isSuccess: false, errors: errors);

    /// <summary>
    /// Creates a successful result containing a value.
    /// </summary>
    public static Result<T> Success<T>(T value)
        => new(value);

    /// <summary>
    /// Creates a failed generic result containing one error.
    /// </summary>
    public static Result<T> Failure<T>(ResultError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(errors: [error]);
    }

    /// <summary>
    /// Creates a failed generic result containing one or more errors.
    /// </summary>
    public static Result<T> Failure<T>(IEnumerable<ResultError> errors)
        => new(errors);

    /// <summary>
    /// Combines results, returning all failure reasons when any input failed.
    /// </summary>
    public static Result Combine(params Result[] results)
        => Combine((IEnumerable<Result>)results);

    /// <summary>
    /// Combines results, returning all failure reasons when any input failed.
    /// </summary>
    public static Result Combine(IEnumerable<Result> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        Result[] resultArray = results.ToArray();

        if (resultArray.Any(static result => result is null))
        {
            throw new ArgumentException("The result collection cannot contain null items.", nameof(results));
        }

        ResultError[] errors = resultArray.Where(static result => result.IsFailure)
                                          .SelectMany(static result => result.Errors)
                                          .ToArray();

        return errors.Length == 0 ? Success() : Failure(errors);
    }

    /// <summary>
    /// Maps the result to one of two values.
    /// </summary>
    public TResult Match<TResult>(Func<TResult> onSuccess, Func<IReadOnlyList<ResultError>, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        return IsSuccess ? onSuccess() : onFailure(Errors);
    }

    /// <summary>
    /// Executes one of two actions based on the result state.
    /// </summary>
    public void Switch(Action onSuccess, Action<IReadOnlyList<ResultError>> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        if (IsSuccess)
        {
            onSuccess();
            return;
        }

        onFailure(Errors);
    }

    /// <summary>
    /// Executes a side effect only when the result succeeded.
    /// </summary>
    public Result Tap(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsSuccess)
        {
            action();
        }

        return this;
    }

    /// <summary>
    /// Executes a side effect only when the result failed.
    /// </summary>
    public Result TapFailure(Action<IReadOnlyList<ResultError>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (IsFailure)
        {
            action(Errors);
        }

        return this;
    }
}
