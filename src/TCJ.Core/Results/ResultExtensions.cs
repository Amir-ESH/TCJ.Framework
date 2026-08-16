namespace TCJ.Core.Results;

/// <summary>
/// Provides small, composable extensions for result values.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Converts a nullable reference to a result.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="errorIfNull">The error if null value.</param>
    /// <returns>The resulting value.</returns>
    public static Result<T> ToResult<T>(this T? value, ResultError errorIfNull) where T : class
    {
        ArgumentNullException.ThrowIfNull(errorIfNull);

        return value is null ? Result.Failure<T>(errorIfNull) : Result.Success(value);
    }

    /// <summary>
    /// Converts a nullable value type to a result.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to inspect or transform.</param>
    /// <param name="errorIfNull">The error if null value.</param>
    /// <returns>The resulting value.</returns>
    public static Result<T> ToResult<T>(this T? value, ResultError errorIfNull) where T : struct
    {
        ArgumentNullException.ThrowIfNull(errorIfNull);

        return value.HasValue ? Result.Success(value.Value) : Result.Failure<T>(errorIfNull);
    }

    /// <summary>
    /// Enables LINQ query syntax by forwarding to the result mapping operation.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="result">The result to convert or process.</param>
    /// <param name="selector">The selector value.</param>
    /// <returns>The result of the operation.</returns>
    public static Result<TResult> Select<T, TResult>(this Result<T> result,
                                                     Func<T, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Map(selector);
    }

    /// <summary>
    /// Enables LINQ query syntax by forwarding to the result binding operation.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TIntermediate">The intermediate type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="result">The result to convert or process.</param>
    /// <param name="selector">The selector value.</param>
    /// <param name="resultSelector">The result selector value.</param>
    /// <returns>The result of the operation.</returns>
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
