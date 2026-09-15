using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.Messaging.Sagas.Configuration;
using TCJ.Messaging.Sagas.Diagnostics;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;
using TCJ.Messaging.Sagas.Remediation;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

internal sealed class SagaRemediationService<TDbContext>(IServiceScopeFactory scopeFactory, TcjSagaOptions options, TimeProvider timeProvider) : ISagaRemediationService
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    public async Task<SagaCompensationResult> RetryCompensationAsync(Guid sagaId, CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        TDbContext dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await scope.ServiceProvider.GetRequiredService<ISagaStartupValidator>().ValidateAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SagaInstance? instance = await dbContext.Set<SagaInstance>().SingleOrDefaultAsync(x => x.SagaId == sagaId, cancellationToken).ConfigureAwait(false);
        if (instance is null || instance.Status != SagaStatus.Compensating || instance.CompensationStatus != SagaCompensationStatus.Pending)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new SagaCompensationResult(sagaId, false, false, false, false);
        }
        if (instance.NextCompensationAttemptAtUtc is { } next && next > now)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new SagaCompensationResult(sagaId, false, false, true, false);
        }

        SagaDefinitionRegistration definition = scope.ServiceProvider.GetRequiredService<SagaDefinitionRegistry>().Get(instance.SagaType);
        if (definition.CompensationHandler is null) throw new SagaValidationException("CompensationNotRegistered");
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = SagaDiagnostics.Start(TcjSagaDiagnosticNames.Activities.Compensate, definition.SagaType, definition.DefinitionVersion, "compensate");
        try
        {
            object state = await SagaStateRuntime.LoadStateAsync(instance, definition, scope.ServiceProvider, options, cancellationToken).ConfigureAwait(false);
            int attempt = instance.CompensationAttemptCount + 1;
            var context = new SagaContext(instance.SagaId, definition.SagaType, definition.DefinitionVersion, instance.CurrentState,
                "compensation", "tcj.saga.compensation", 0, attempt, null, null, now);
            await definition.CompensationHandler(scope.ServiceProvider, state, context, cancellationToken).ConfigureAwait(false);
            if (context.LifecycleAction != SagaLifecycleAction.CompleteCompensation)
                throw new SagaValidationException("ExplicitCompensationCompletionRequired");
            instance.CompensationAttemptCount = attempt;
            await SagaStateRuntime.ApplyContextAsync(dbContext, instance, definition, context, state, options, now, requireExplicitLifecycle: true, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            SagaDiagnostics.Complete(activity, definition.SagaType, definition.DefinitionVersion, "compensate", "success", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            SagaDiagnostics.RecordCompensation(definition.SagaType, "success");
            return new SagaCompensationResult(sagaId, true, true, false, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            SagaDiagnostics.Complete(activity, definition.SagaType, definition.DefinitionVersion, "compensate", "failure", Stopwatch.GetElapsedTime(started).TotalMilliseconds, exception.GetType().Name);
            return await RecordCompensationFailureAsync(sagaId, definition.SagaType, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<SagaCleanupResult> CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (options.TerminalRetentionPeriod == TimeSpan.Zero) return new SagaCleanupResult(0, true);
        using IServiceScope scope = scopeFactory.CreateScope();
        TDbContext dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await scope.ServiceProvider.GetRequiredService<ISagaStartupValidator>().ValidateAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset threshold = timeProvider.GetUtcNow() - options.TerminalRetentionPeriod;
        using Activity? activity = SagaDiagnostics.Start(TcjSagaDiagnosticNames.Activities.Cleanup, "bounded-terminal", 0, "cleanup");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Keep cleanup on EF's tracked optimistic-concurrency path. The SQL Server package
        // configures SagaInstance.ConcurrencyToken as rowversion, so a transition/remediation
        // that wins after this snapshot causes the delete to fail instead of removing stale state.
        SagaInstance[] candidates = await dbContext.Set<SagaInstance>()
            .Where(x => (x.Status == SagaStatus.Completed || x.Status == SagaStatus.Failed || x.Status == SagaStatus.Compensated) && x.UpdatedAtUtc < threshold)
            .Where(x => x.CompensationStatus != SagaCompensationStatus.Pending)
            .Where(x => !dbContext.Set<SagaTimer>().Any(timer => timer.SagaId == x.SagaId && timer.Status == SagaTimerStatus.Scheduled))
            .OrderBy(x => x.UpdatedAtUtc).ThenBy(x => x.SagaId)
            .Take(options.CleanupBatchSize)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        if (candidates.Length == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new SagaCleanupResult(0, false);
        }

        Guid[] candidateIds = candidates.Select(static instance => instance.SagaId).ToArray();
        await dbContext.Set<SagaCorrelation>()
            .Where(correlation => candidateIds.Contains(correlation.SagaId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        dbContext.Set<SagaInstance>().RemoveRange(candidates);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        SagaDiagnostics.Complete(activity, "bounded-terminal", 0, "cleanup", "success", 0);
        return new SagaCleanupResult(candidates.Length, false);
    }

    private async Task<SagaCompensationResult> RecordCompensationFailureAsync(Guid sagaId, string sagaType, Exception exception, CancellationToken cancellationToken)
    {
        using IServiceScope failureScope = scopeFactory.CreateScope();
        TDbContext dbContext = failureScope.ServiceProvider.GetRequiredService<TDbContext>();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SagaInstance? instance = await dbContext.Set<SagaInstance>().SingleOrDefaultAsync(x => x.SagaId == sagaId, cancellationToken).ConfigureAwait(false);
        if (instance is null || instance.Status != SagaStatus.Compensating || instance.CompensationStatus != SagaCompensationStatus.Pending)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new SagaCompensationResult(sagaId, true, false, false, false);
        }
        int attempt = instance.CompensationAttemptCount + 1;
        bool retry = attempt < options.MaxCompensationAttempts && exception is not SagaValidationException;
        instance.CompensationAttemptCount = attempt;
        instance.LastCompensationFailureType = exception.GetType().Name;
        instance.UpdatedAtUtc = timeProvider.GetUtcNow();
        if (retry)
        {
            instance.NextCompensationAttemptAtUtc = instance.UpdatedAtUtc + ComputeDelay(options.CompensationRetryBaseDelay, attempt);
        }
        else
        {
            instance.CompensationStatus = SagaCompensationStatus.Exhausted;
            instance.NextCompensationAttemptAtUtc = null;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        SagaDiagnostics.RecordCompensation(sagaType, retry ? "retry" : "exhausted");
        return new SagaCompensationResult(sagaId, true, false, retry, !retry);
    }

    private static TimeSpan ComputeDelay(TimeSpan baseDelay, int attempt)
    {
        int exponent = Math.Clamp(attempt - 1, 0, 8);
        double ticks = Math.Min(baseDelay.Ticks * Math.Pow(2, exponent), TimeSpan.FromHours(1).Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }
}
