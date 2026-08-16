namespace TCJ.Core.Results;

/// <summary>
/// Provides collection-level result operations.
/// </summary>
public static class ResultCollectionExtensions
{
    /// <summary>
    /// Combines generic results into one result containing all successful values.
    /// Every failure error is preserved.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="results">The results value.</param>
    /// <returns>The result of the operation.</returns>
    public static Result<IReadOnlyList<T>> Combine<T>(this IEnumerable<Result<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        Result<T>[] resultArray = results.ToArray();

        if (resultArray.Any(static result => result is null))
        {
            throw new ArgumentException("The result collection cannot contain null items.", nameof(results));
        }

        ResultError[] errors = resultArray.Where(static result => result.IsFailure)
                                          .SelectMany(static result => result.Errors)
                                          .ToArray();

        if (errors.Length > 0)
        {
            return Result.Failure<IReadOnlyList<T>>(errors);
        }

        T[] values = resultArray.Select(static result => result.Value)
                                .ToArray();

        return Result.Success<IReadOnlyList<T>>(values);
    }

    /// <summary>
    /// Maps every source item to a result and combines the outcomes.
    /// </summary>
    /// <typeparam name="TSource">The source type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="source">The source collection or sequence.</param>
    /// <param name="selector">The selector value.</param>
    /// <returns>The result of the operation.</returns>
    public static Result<IReadOnlyList<TResult>> Traverse<TSource, TResult>(this IEnumerable<TSource> source,
                                                                            Func<TSource, Result<TResult>> selector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);

        return source.Select(selector).Combine();
    }

    /// <summary>
    /// Asynchronously maps every source item to a result and combines the outcomes.
    /// </summary>
    /// <typeparam name="TSource">The source type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="source">The source collection or sequence.</param>
    /// <param name="selector">The selector value.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<Result<IReadOnlyList<TResult>>> TraverseAsync<TSource, TResult>(
        this IEnumerable<TSource> source,
        Func<TSource, CancellationToken, Task<Result<TResult>>> selector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);

        var results = new List<Result<TResult>>();

        foreach (TSource item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Result<TResult> result = await selector(item, cancellationToken).ConfigureAwait(false)
                                  ?? throw new InvalidOperationException("A traverse selector cannot return null.");

            results.Add(result);
        }

        return results.Combine();
    }

    /// <summary>
    /// Returns the values from successful results.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="results">The results value.</param>
    /// <returns>The result of the operation.</returns>
    public static IEnumerable<T> WhereSuccess<T>(this IEnumerable<Result<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return results.Where(static result => result.IsSuccess)
                      .Select(static result => result.Value);
    }

    /// <summary>
    /// Returns every error from failed results.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="results">The results value.</param>
    /// <returns>The result of the operation.</returns>
    public static IEnumerable<ResultError> WhereFailure<T>(this IEnumerable<Result<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return results.Where(static result => result.IsFailure)
                      .SelectMany(static result => result.Errors);
    }

    /// <summary>
    /// Partitions results into successful values and failure errors.
    /// </summary>
    /// <typeparam name="T">The value type contained by successful results.</typeparam>
    /// <param name="results">The results to partition.</param>
    /// <returns>A tuple containing the successful values and the errors from failed results.</returns>
    public static (IReadOnlyList<T> Successes, IReadOnlyList<ResultError> Failures) Partition<T>(this IEnumerable<Result<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        Result<T>[] resultArray = results.ToArray();

        return (Successes: resultArray.WhereSuccess().ToArray(), Failures: resultArray.WhereFailure().ToArray());
    }
}
