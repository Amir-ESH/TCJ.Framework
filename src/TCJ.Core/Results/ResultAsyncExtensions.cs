namespace TCJ.Core.Results;

/// <summary>
/// Provides asynchronous result composition helpers.
/// </summary>
public static class ResultAsyncExtensions
{
    /// <summary>
    /// Asynchronously transforms the value of a successful result.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="result">The result to convert or process.</param>
    /// <param name="mapper">The mapper value.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The resulting value.</returns>
    public static async Task<Result<TResult>> MapAsync<T, TResult>(this Result<T> result,
                                                                   Func<T, CancellationToken, Task<TResult>> mapper,
                                                                   CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(mapper);

        if (result.IsFailure)
        {
            return Result.Failure<TResult>(result.Errors);
        }

        TResult value = await mapper(result.Value, cancellationToken).ConfigureAwait(false);
        return Result.Success(value);
    }
    /// <summary>
    /// Asynchronously chains a result-producing operation when the source result is successful.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="result">The result to convert or process.</param>
    /// <param name="binder">The binder value.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The resulting value.</returns>

    public static async Task<Result<TResult>> BindAsync<T, TResult>(this Result<T> result,
                                                                    Func<T, CancellationToken, Task<Result<TResult>>> binder,
                                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(binder);

        if (result.IsFailure)
        {
            return Result.Failure<TResult>(result.Errors);
        }

        return await binder(result.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A result binder cannot return null.");
    }
    /// <summary>
    /// Asynchronously executes a side effect for a successful result while preserving the result.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result to convert or process.</param>
    /// <param name="action">The action value.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The resulting value.</returns>

    public static async Task<Result<T>> TapAsync<T>(this Result<T> result,
                                                    Func<T, CancellationToken, Task> action,
                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(action);

        if (result.IsSuccess)
        {
            await action(result.Value, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
    /// <summary>
    /// Asynchronously validates a successful result against an additional predicate.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result to convert or process.</param>
    /// <param name="predicate">The predicate used to evaluate values.</param>
    /// <param name="error">The error value.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The resulting value.</returns>

    public static async Task<Result<T>> EnsureAsync<T>(this Result<T> result,
                                                       Func<T, CancellationToken, Task<bool>> predicate,
                                                       ResultError error,
                                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(error);

        if (result.IsFailure)
        {
            return result;
        }

        return await predicate(result.Value, cancellationToken).ConfigureAwait(false)
                   ? result
                   : Result.Failure<T>(error);
    }
    /// <summary>
    /// Asynchronously maps the result to one of two outcomes based on success or failure.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="result">The result to convert or process.</param>
    /// <param name="onSuccess">The delegate invoked for a successful result.</param>
    /// <param name="onFailure">The on failure value.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The resulting value.</returns>

    public static async Task<TResult> MatchAsync<T, TResult>(this Result<T> result,
                                                             Func<T, CancellationToken, Task<TResult>> onSuccess,
                                                             Func<IReadOnlyList<ResultError>, CancellationToken, Task<TResult>> onFailure,
                                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        return result.IsSuccess
            ? await onSuccess(result.Value, cancellationToken).ConfigureAwait(false)
            : await onFailure(result.Errors, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// Transforms the value of a successful result.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="resultTask">The result task value.</param>
    /// <param name="mapper">The mapper value.</param>
    /// <returns>The resulting value.</returns>

    public static async Task<Result<TResult>> Map<T, TResult>(this Task<Result<T>> resultTask,
                                                              Func<T, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(resultTask);
        ArgumentNullException.ThrowIfNull(mapper);

        Result<T> result = await resultTask.ConfigureAwait(false);
        return result.Map(mapper);
    }
    /// <summary>
    /// Chains a result-producing operation when the source result is successful.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="resultTask">The result task value.</param>
    /// <param name="binder">The binder value.</param>
    /// <returns>The resulting value.</returns>

    public static async Task<Result<TResult>> Bind<T, TResult>(this Task<Result<T>> resultTask,
                                                               Func<T, Result<TResult>> binder)
    {
        ArgumentNullException.ThrowIfNull(resultTask);
        ArgumentNullException.ThrowIfNull(binder);

        Result<T> result = await resultTask.ConfigureAwait(false);
        return result.Bind(binder);
    }
    /// <summary>
    /// Chains a result-producing operation when the source result is successful.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="resultTask">The result task value.</param>
    /// <param name="binder">The binder value.</param>
    /// <returns>The resulting value.</returns>

    public static async Task<Result<TResult>> Bind<T, TResult>(this Task<Result<T>> resultTask,
                                                               Func<T, Task<Result<TResult>>> binder)
    {
        ArgumentNullException.ThrowIfNull(resultTask);
        ArgumentNullException.ThrowIfNull(binder);

        Result<T> result = await resultTask.ConfigureAwait(false);

        if (result.IsFailure)
        {
            return Result.Failure<TResult>(result.Errors);
        }

        return await binder(result.Value).ConfigureAwait(false)
               ?? throw new InvalidOperationException("A result binder cannot return null.");
    }
    /// <summary>
    /// Validates a successful result against an additional predicate.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="resultTask">The result task value.</param>
    /// <param name="predicate">The predicate used to evaluate values.</param>
    /// <param name="error">The error value.</param>
    /// <returns>The resulting value.</returns>

    public static async Task<Result<T>> Ensure<T>(this Task<Result<T>> resultTask,
                                                  Func<T, bool> predicate,
                                                  ResultError error)
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        Result<T> result = await resultTask.ConfigureAwait(false);
        return result.Ensure(predicate, error);
    }
    /// <summary>
    /// Executes a side effect for a successful result while preserving the result.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="resultTask">The result task value.</param>
    /// <param name="action">The action value.</param>
    /// <returns>The resulting value.</returns>

    public static async Task<Result<T>> Tap<T>(this Task<Result<T>> resultTask,
                                               Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        Result<T> result = await resultTask.ConfigureAwait(false);
        return result.Tap(action);
    }
    /// <summary>
    /// Maps the result to one of two outcomes based on success or failure.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="resultTask">The result task value.</param>
    /// <param name="onSuccess">The delegate invoked for a successful result.</param>
    /// <param name="onFailure">The on failure value.</param>
    /// <returns>The resulting value.</returns>

    public static async Task<TResult> Match<T, TResult>(this Task<Result<T>> resultTask,
                                                        Func<T, TResult> onSuccess,
                                                        Func<IReadOnlyList<ResultError>, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        Result<T> result = await resultTask.ConfigureAwait(false);
        return result.Match(onSuccess, onFailure);
    }
}
