namespace TCJ.Core.Results;

/// <summary>
/// Provides small, composable extensions for result values.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Converts a nullable reference to a result.
    /// </summary>
    public static Result<T> ToResult<T>(this T? value, ResultError errorIfNull) where T : class
    {
        ArgumentNullException.ThrowIfNull(errorIfNull);

        return value is null ? Result.Failure<T>(errorIfNull) : Result.Success(value);
    }

    /// <summary>
    /// Converts a nullable value type to a result.
    /// </summary>
    public static Result<T> ToResult<T>(this T? value, ResultError errorIfNull) where T : struct
    {
        ArgumentNullException.ThrowIfNull(errorIfNull);

        return value.HasValue ? Result.Success(value.Value) : Result.Failure<T>(errorIfNull);
    }

    /// <summary>
    /// Enables LINQ query syntax by forwarding to the result mapping operation.
    /// </summary>
    public static Result<TResult> Select<T, TResult>(this Result<T> result,
                                                     Func<T, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Map(selector);
    }

    /// <summary>
    /// Enables LINQ query syntax by forwarding to the result binding operation.
    /// </summary>
    public static Result<TResult> SelectMany<T, TIntermediate, TResult>(this Result<T> result,
                                                                        Func<T, Result<TIntermediate>> selector,
                                                                        Func<T, TIntermediate, TResult> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resultSelector);

        return result.Bind(value => selector(value).Map(intermediate => resultSelector(value, intermediate)));
    }
}
