using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Diagnostics;

namespace TCJ.EntityFrameworkCore.SqlServer.Extensions;

/// <summary>
/// Provides transaction-level SQL Server resilience helpers that delegate retry
/// classification and scheduling to the configured EF Core execution strategy.
/// </summary>
public static class SqlServerResilienceExtensions
{
    /// <summary>
    /// Executes a complete SQL Server transaction as one retriable unit. A fresh
    /// dependency-injection scope, DbContext, and transaction are created for every
    /// execution-strategy attempt so failed state is never reused.
    /// </summary>
    /// <typeparam name="TDbContext">The registered SQL Server DbContext type.</typeparam>
    /// <param name="scopeFactory">Application scope factory.</param>
    /// <param name="operation">
    /// Transaction body. The delegate owns SaveChanges calls; TCJ commits only after
    /// the delegate completes successfully.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>A task that completes after the transaction has committed successfully.</returns>
    public static async Task ExecuteTcjSqlServerTransactionAsync<TDbContext>(
        this IServiceScopeFactory scopeFactory,
        Func<TDbContext, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(operation);
        await scopeFactory.ExecuteTcjSqlServerTransactionAsync<TDbContext, object?>(
            async (dbContext, token) =>
            {
                await operation(dbContext, token).ConfigureAwait(false);
                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a complete SQL Server transaction as one retriable unit and returns
    /// the successful delegate result.
    /// </summary>
    /// <typeparam name="TDbContext">The registered SQL Server DbContext type.</typeparam>
    /// <typeparam name="TResult">The transaction result type.</typeparam>
    /// <param name="scopeFactory">Application scope factory.</param>
    /// <param name="operation">Transaction body executed with a fresh context on every attempt.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>The result from the successfully committed attempt.</returns>
    public static async Task<TResult> ExecuteTcjSqlServerTransactionAsync<TDbContext, TResult>(
        this IServiceScopeFactory scopeFactory,
        Func<TDbContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(operation);

        await using AsyncServiceScope strategyScope = scopeFactory.CreateAsyncScope();
        TDbContext strategyContext = strategyScope.ServiceProvider.GetRequiredService<TDbContext>();
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        const string strategyName = "sqlserver_transaction";
        int attempt = 0;

        using Activity? activity = ResilienceTelemetryDiagnostics.Start(
            TcjDiagnosticNames.Activities.ResilienceExecute,
            strategyName);

        try
        {
            TResult result = await strategy.ExecuteAsync(async retryToken =>
            {
                int currentAttempt = Interlocked.Increment(ref attempt);
                if (currentAttempt > 1)
                {
                    ResilienceTelemetryDiagnostics.RecordRetry(
                        strategyName,
                        currentAttempt,
                        "provider_transient");
                }

                await using AsyncServiceScope attemptScope = scopeFactory.CreateAsyncScope();
                TDbContext dbContext = attemptScope.ServiceProvider.GetRequiredService<TDbContext>();
                await using var transaction = await dbContext.Database
                    .BeginTransactionAsync(retryToken)
                    .ConfigureAwait(false);

                try
                {
                    TResult operationResult = await operation(dbContext, retryToken)
                        .ConfigureAwait(false);
                    await transaction.CommitAsync(retryToken).ConfigureAwait(false);
                    ResilienceTelemetryDiagnostics.RecordAttempt(
                        strategyName,
                        TcjDiagnosticNames.Outcomes.Success,
                        currentAttempt);
                    return operationResult;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ResilienceTelemetryDiagnostics.RecordAttempt(
                        strategyName,
                        TcjDiagnosticNames.Outcomes.Canceled,
                        currentAttempt,
                        "canceled");
                    throw;
                }
                catch
                {
                    // EF's provider execution strategy owns transient classification.
                    // The transaction is disposed without commit and a future retry gets
                    // a fresh scope/context/transaction.
                    ResilienceTelemetryDiagnostics.RecordAttempt(
                        strategyName,
                        TcjDiagnosticNames.Outcomes.Failure,
                        currentAttempt,
                        "provider_failure");
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);

            TcjTelemetry.CompleteSuccess(activity);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TcjTelemetry.CompleteCanceled(activity);
            throw;
        }
        catch (Exception exception)
        {
            ResilienceTelemetryDiagnostics.RecordFailure(strategyName, "provider_failure");
            TcjTelemetry.CompleteFailure(activity, exception);
            throw;
        }
    }
}
