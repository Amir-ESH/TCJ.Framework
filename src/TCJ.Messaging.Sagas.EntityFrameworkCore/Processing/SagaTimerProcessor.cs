using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Resilience;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.Messaging.Sagas.Configuration;
using TCJ.Messaging.Sagas.Diagnostics;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;
using TCJ.Messaging.Sagas.Processing;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

internal sealed class SagaTimerProcessor<TDbContext>(IServiceScopeFactory scopeFactory, TcjSagaOptions options, TimeProvider timeProvider) : ISagaTimerProcessor
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    public async Task<SagaTimerProcessingResult> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SagaTimerClaim> claims;
        using (IServiceScope claimScope = scopeFactory.CreateScope())
        {
            ISagaStartupValidator validator = claimScope.ServiceProvider.GetRequiredService<ISagaStartupValidator>();
            await validator.ValidateAsync(cancellationToken).ConfigureAwait(false);
            claims = await claimScope.ServiceProvider.GetRequiredService<ISagaProviderStorage>()
                .ClaimDueTimersAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        if (claims.Count == 0) return SagaTimerProcessingResult.Empty;

        int completed = 0;
        int retries = 0;
        int failed = 0;
        foreach (SagaTimerClaim claim in claims)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimerOutcome outcome = await ProcessClaimAsync(claim, cancellationToken).ConfigureAwait(false);
            if (outcome == TimerOutcome.Completed) completed++;
            else if (outcome == TimerOutcome.Retry) retries++;
            else failed++;
        }
        return new SagaTimerProcessingResult(claims.Count, completed, retries, failed);
    }

    private async Task<TimerOutcome> ProcessClaimAsync(SagaTimerClaim claim, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = SagaDiagnostics.Start(TcjSagaDiagnosticNames.Activities.Timeout, claim.SagaType, 0, "timeout", timerName: claim.TimerName);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            TDbContext dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
            SagaDefinitionRegistry registry = scope.ServiceProvider.GetRequiredService<SagaDefinitionRegistry>();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            SagaTimer? timer = await dbContext.Set<SagaTimer>().SingleOrDefaultAsync(
                x => x.TimerId == claim.TimerId && x.LockId == claim.LockId && x.Status == SagaTimerStatus.Scheduled,
                cancellationToken).ConfigureAwait(false);
            if (timer is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                SagaDiagnostics.RecordTimer(claim.SagaType, "stale-claim", retry: false);
                return TimerOutcome.Completed;
            }

            SagaInstance instance = await dbContext.Set<SagaInstance>().SingleOrDefaultAsync(x => x.SagaId == claim.SagaId, cancellationToken).ConfigureAwait(false)
                ?? throw new SagaValidationException("TimerSagaMissing");
            SagaDefinitionRegistration definition = registry.Get(claim.SagaType);
            if (!definition.Timers.TryGetValue(claim.TimerName, out SagaTimeoutRegistration? timeoutDefinition)
                || !string.Equals(timeoutDefinition.TimeoutType, claim.TimeoutType, StringComparison.Ordinal))
                throw new SagaValidationException("UnknownTimerContract");

            if (instance.Status is SagaStatus.Completed or SagaStatus.Failed or SagaStatus.Compensated)
            {
                timer.Status = SagaTimerStatus.Canceled;
                timer.LockId = null;
                timer.LockedAtUtc = null;
                timer.LockExpiresAtUtc = null;
                timer.UpdatedAtUtc = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                SagaDiagnostics.RecordTimer(claim.SagaType, "terminal-canceled", retry: false);
                return TimerOutcome.Completed;
            }

            if (!timeoutDefinition.AllowedStates.Contains(instance.CurrentState))
                ApplyInvalidPolicy(timeoutDefinition.InvalidTransitionPolicy, "TimeoutOutOfOrderState");

            object state = await SagaStateRuntime.LoadStateAsync(instance, definition, scope.ServiceProvider, options, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = timeProvider.GetUtcNow();
            var context = new SagaContext(instance.SagaId, definition.SagaType, definition.DefinitionVersion, instance.CurrentState,
                $"timer:{timer.TimerId:N}", timeoutDefinition.TimeoutType, 0, claim.Attempt, null, null, now);
            var timeout = new SagaTimeout(timer.TimerName, timer.TimeoutType, timer.DueAtUtc, claim.Attempt);
            await timeoutDefinition.Invoke(scope.ServiceProvider, state, timeout, context, cancellationToken).ConfigureAwait(false);
            await SagaStateRuntime.ApplyContextAsync(dbContext, instance, definition, context, state, options, now, requireExplicitLifecycle: true, cancellationToken).ConfigureAwait(false);

            bool explicitlyMutatedCurrentTimer = context.TimerMutations.Any(mutation => string.Equals(mutation.TimerName, timer.TimerName, StringComparison.Ordinal));
            if (!explicitlyMutatedCurrentTimer)
            {
                timer.Status = SagaTimerStatus.Completed;
                timer.CompletedAtUtc = now;
                timer.LockId = null;
                timer.LockedAtUtc = null;
                timer.LockExpiresAtUtc = null;
                timer.LastFailureType = null;
                timer.UpdatedAtUtc = now;
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            SagaDiagnostics.Complete(activity, definition.SagaType, definition.DefinitionVersion, "timeout", "success", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            SagaDiagnostics.RecordTimer(definition.SagaType, "success", retry: false);
            return TimerOutcome.Completed;
        }
        catch (SagaIgnoreException)
        {
            return await CompleteIgnoredTimerAsync(claim, activity, started, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            using IServiceScope failureScope = scopeFactory.CreateScope();
            ITransientFailureDetector detector = failureScope.ServiceProvider.GetRequiredService<ITransientFailureDetector>();
            bool retry = claim.Attempt < options.MaxTimerAttempts && IsRetryableTimerFailure(exception, detector);
            DateTimeOffset now = timeProvider.GetUtcNow();
            DateTimeOffset? next = retry ? now + ComputeDelay(options.TimerRetryBaseDelay, claim.Attempt) : null;
            await failureScope.ServiceProvider.GetRequiredService<ISagaProviderStorage>()
                .RecordTimerFailureAsync(claim.TimerId, claim.LockId, claim.Attempt, exception.GetType().Name, retry, next, now, cancellationToken)
                .ConfigureAwait(false);
            SagaDiagnostics.Complete(activity, claim.SagaType, 0, "timeout", retry ? "retry" : "failed", Stopwatch.GetElapsedTime(started).TotalMilliseconds, exception.GetType().Name);
            SagaDiagnostics.RecordTimer(claim.SagaType, retry ? "retry" : "failed", retry);
            return retry ? TimerOutcome.Retry : TimerOutcome.Failed;
        }
    }

    private async Task<TimerOutcome> CompleteIgnoredTimerAsync(SagaTimerClaim claim, Activity? activity, long started, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        TDbContext dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SagaTimer? timer = await dbContext.Set<SagaTimer>().SingleOrDefaultAsync(x => x.TimerId == claim.TimerId && x.LockId == claim.LockId, cancellationToken).ConfigureAwait(false);
        if (timer is not null)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            timer.Status = SagaTimerStatus.Completed;
            timer.CompletedAtUtc = now;
            timer.LockId = null;
            timer.LockedAtUtc = null;
            timer.LockExpiresAtUtc = null;
            timer.UpdatedAtUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        SagaDiagnostics.Complete(activity, claim.SagaType, 0, "timeout", "ignored", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        SagaDiagnostics.RecordTimer(claim.SagaType, "ignored", retry: false);
        return TimerOutcome.Completed;
    }

    private static void ApplyInvalidPolicy(SagaInvalidTransitionPolicy policy, string reason)
    {
        switch (policy)
        {
            case SagaInvalidTransitionPolicy.Retry: throw new SagaRetryException(reason);
            case SagaInvalidTransitionPolicy.DeadLetter: throw new SagaValidationException(reason);
            case SagaInvalidTransitionPolicy.Ignore: throw new SagaIgnoreException();
            default: throw new SagaValidationException("UnknownInvalidTransitionPolicy");
        }
    }

    private static bool IsRetryableTimerFailure(Exception exception, ITransientFailureDetector detector)
    {
        if (exception is SagaValidationException) return false;
        if (exception is SagaRetryException or DbUpdateConcurrencyException or TimeoutException) return true;
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (detector.IsTransient(current)) return true;
        return false;
    }

    private static TimeSpan ComputeDelay(TimeSpan baseDelay, int attempt)
    {
        int exponent = Math.Clamp(attempt - 1, 0, 8);
        double ticks = Math.Min(baseDelay.Ticks * Math.Pow(2, exponent), TimeSpan.FromHours(1).Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private enum TimerOutcome { Completed, Retry, Failed }
    private sealed class SagaIgnoreException : Exception;
}
