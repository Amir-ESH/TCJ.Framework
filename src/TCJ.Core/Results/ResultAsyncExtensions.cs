namespace TCJ.Core.Results;

/// <summary>
/// Provides asynchronous result composition helpers.
/// </summary>
public static class ResultAsyncExtensions
{
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

    public static async Task<Result<TResult>> Map<T, TResult>(this Task<Result<T>> resultTask,
                                                              Func<T, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(resultTask);
        ArgumentNullException.ThrowIfNull(mapper);

        Result<T> result = await resultTask.ConfigureAwait(false);
        return result.Map(mapper);
    }

    public static async Task<Result<TResult>> Bind<T, TResult>(this Task<Result<T>> resultTask,
                                                               Func<T, Result<TResult>> binder)
    {
        ArgumentNullException.ThrowIfNull(resultTask);
        ArgumentNullException.ThrowIfNull(binder);

        Result<T> result = await resultTask.ConfigureAwait(false);
        return result.Bind(binder);
    }

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

    public static async Task<Result<T>> Ensure<T>(this Task<Result<T>> resultTask,
                                                  Func<T, bool> predicate,
                                                  ResultError error)
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        Result<T> result = await resultTask.ConfigureAwait(false);
        return result.Ensure(predicate, error);
    }

    public static async Task<Result<T>> Tap<T>(this Task<Result<T>> resultTask,
                                               Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        Result<T> result = await resultTask.ConfigureAwait(false);
        return result.Tap(action);
    }

    public static async Task<TResult> Match<T, TResult>(this Task<Result<T>> resultTask,
                                                        Func<T, TResult> onSuccess,
                                                        Func<IReadOnlyList<ResultError>, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        Result<T> result = await resultTask.ConfigureAwait(false);
        return result.Match(onSuccess, onFailure);
    }
}
