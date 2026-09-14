using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Inbox;
using TCJ.Messaging.Sagas.Configuration;
using TCJ.Messaging.Sagas.Diagnostics;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;
using TCJ.Messaging.Sagas.Migration;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

internal static class SagaStateRuntime
{
    internal static string HashCorrelation(SagaCorrelationKey correlation) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(correlation.CanonicalValue)));

    internal static async Task<object> LoadStateAsync(
        SagaInstance instance,
        SagaDefinitionRegistration definition,
        IServiceProvider services,
        TcjSagaOptions options,
        CancellationToken cancellationToken)
    {
        ValidateDefinitionVersion(instance, definition);
        if (instance.StateSchemaVersion != definition.StateSchemaVersion)
        {
            string payload = instance.StatePayload;
            int version = instance.StateSchemaVersion;
            long started = Stopwatch.GetTimestamp();
            using Activity? activity = SagaDiagnostics.Start(TcjSagaDiagnosticNames.Activities.Migrate, definition.SagaType, definition.DefinitionVersion, "migrate");
            try
            {
                while (version < definition.StateSchemaVersion)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!definition.Migrators.TryGetValue(version, out SagaMigratorRegistration? edge))
                        throw new SagaValidationException("UnsupportedStateSchemaVersion");
                    var migrator = (ISagaStateMigrator)services.GetRequiredService(edge.MigratorType);
                    if (migrator.FromVersion != edge.FromVersion || migrator.ToVersion != edge.ToVersion)
                        throw new SagaValidationException("StateMigratorMetadataMismatch");
                    payload = migrator.Migrate(payload);
                    EnsurePayloadSize(payload, options);
                    version = edge.ToVersion;
                }
                instance.StatePayload = payload;
                instance.StateSchemaVersion = version;
                instance.DefinitionVersion = definition.DefinitionVersion;
                SagaDiagnostics.Complete(activity, definition.SagaType, definition.DefinitionVersion, "migrate", "success", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception exception)
            {
                SagaDiagnostics.Complete(activity, definition.SagaType, definition.DefinitionVersion, "migrate", "failure", Stopwatch.GetElapsedTime(started).TotalMilliseconds, exception.GetType().Name);
                throw;
            }
        }
        else if (instance.DefinitionVersion != definition.DefinitionVersion)
        {
            instance.DefinitionVersion = definition.DefinitionVersion;
        }

        try
        {
            return JsonSerializer.Deserialize(instance.StatePayload, definition.StateJsonTypeInfo)
                ?? throw new SagaValidationException("NullSagaState");
        }
        catch (JsonException exception)
        {
            throw new SagaValidationException("InvalidStatePayload", exception);
        }
    }

    internal static string SerializeState(object state, SagaDefinitionRegistration definition, TcjSagaOptions options)
    {
        string payload;
        try { payload = JsonSerializer.Serialize(state, definition.StateJsonTypeInfo); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new SagaValidationException("StateSerialization", exception);
        }
        EnsurePayloadSize(payload, options);
        return payload;
    }

    internal static async Task ApplyContextAsync<TDbContext>(
        TDbContext dbContext,
        SagaInstance instance,
        SagaDefinitionRegistration definition,
        SagaContext context,
        object state,
        TcjSagaOptions options,
        DateTimeOffset now,
        bool requireExplicitLifecycle,
        CancellationToken cancellationToken)
        where TDbContext : DbContext
    {
        if (requireExplicitLifecycle && context.LifecycleAction == SagaLifecycleAction.None)
            throw new SagaValidationException("ExplicitTransitionRequired");

        switch (context.LifecycleAction)
        {
            case SagaLifecycleAction.None:
            case SagaLifecycleAction.Stay:
                break;
            case SagaLifecycleAction.Transition:
                instance.CurrentState = context.NextState!;
                break;
            case SagaLifecycleAction.Complete:
                instance.CurrentState = definition.CompletedState!;
                instance.Status = SagaStatus.Completed;
                instance.CompletedAtUtc = now;
                await CancelAllActiveTimersAsync(dbContext, instance.SagaId, now, cancellationToken).ConfigureAwait(false);
                await RetireActiveCorrelationsAsync(dbContext, instance.SagaId, now, cancellationToken).ConfigureAwait(false);
                SagaDiagnostics.RecordCompleted(definition.SagaType);
                break;
            case SagaLifecycleAction.Fail:
                instance.CurrentState = definition.FailedState!;
                instance.Status = SagaStatus.Failed;
                instance.FailedAtUtc = now;
                await CancelAllActiveTimersAsync(dbContext, instance.SagaId, now, cancellationToken).ConfigureAwait(false);
                await RetireActiveCorrelationsAsync(dbContext, instance.SagaId, now, cancellationToken).ConfigureAwait(false);
                SagaDiagnostics.RecordFailed(definition.SagaType);
                break;
            case SagaLifecycleAction.RequestCompensation:
                if (definition.CompensationHandler is null) throw new SagaValidationException("CompensationNotRegistered");
                instance.CurrentState = definition.CompensatingState!;
                instance.Status = SagaStatus.Compensating;
                instance.CompensationStatus = SagaCompensationStatus.Pending;
                instance.NextCompensationAttemptAtUtc = now;
                await CancelAllActiveTimersAsync(dbContext, instance.SagaId, now, cancellationToken).ConfigureAwait(false);
                break;
            case SagaLifecycleAction.CompleteCompensation:
                if (instance.Status != SagaStatus.Compensating || instance.CompensationStatus != SagaCompensationStatus.Pending)
                    throw new SagaValidationException("CompensationNotPending");
                instance.CurrentState = definition.CompensatedState!;
                instance.Status = SagaStatus.Compensated;
                instance.CompensationStatus = SagaCompensationStatus.Completed;
                instance.NextCompensationAttemptAtUtc = null;
                instance.CompletedAtUtc = now;
                await CancelAllActiveTimersAsync(dbContext, instance.SagaId, now, cancellationToken).ConfigureAwait(false);
                await RetireActiveCorrelationsAsync(dbContext, instance.SagaId, now, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new SagaValidationException("UnknownLifecycleAction");
        }

        foreach (SagaTimerMutation mutation in context.TimerMutations)
        {
            if (!definition.Timers.TryGetValue(mutation.TimerName, out SagaTimeoutRegistration? timerDefinition))
                throw new SagaValidationException("UnknownTimerName");
            SagaTimer? timer = await dbContext.Set<SagaTimer>().SingleOrDefaultAsync(x => x.SagaId == instance.SagaId && x.TimerName == mutation.TimerName, cancellationToken).ConfigureAwait(false);
            if (mutation.Kind == SagaTimerMutationKind.Cancel)
            {
                if (timer is not null && timer.Status == SagaTimerStatus.Scheduled)
                {
                    timer.Status = SagaTimerStatus.Canceled;
                    timer.LockId = null;
                    timer.LockedAtUtc = null;
                    timer.LockExpiresAtUtc = null;
                    timer.UpdatedAtUtc = now;
                }
                continue;
            }
            if (instance.Status is SagaStatus.Completed or SagaStatus.Failed or SagaStatus.Compensated)
                throw new SagaValidationException("TimerOnTerminalSaga");
            if (timer is null)
            {
                timer = new SagaTimer(Guid.CreateVersion7(now), instance.SagaId, definition.SagaType, mutation.TimerName, timerDefinition.TimeoutType, mutation.DueAtUtc!.Value, now);
                dbContext.Set<SagaTimer>().Add(timer);
            }
            else
            {
                timer.DueAtUtc = mutation.DueAtUtc!.Value;
                timer.Status = SagaTimerStatus.Scheduled;
                timer.AttemptCount = 0;
                timer.LockId = null;
                timer.LockedAtUtc = null;
                timer.LockExpiresAtUtc = null;
                timer.CompletedAtUtc = null;
                timer.LastFailureType = null;
                timer.UpdatedAtUtc = now;
            }
        }

        foreach (var domainEvent in context.OutgoingEvents) instance.AddDomainEvent(domainEvent);
        instance.StatePayload = SerializeState(state, definition, options);
        instance.StateSchemaVersion = definition.StateSchemaVersion;
        instance.DefinitionVersion = definition.DefinitionVersion;
        instance.UpdatedAtUtc = now;
    }

    internal static void ValidateDefinition(SagaDefinitionRegistration definition)
    {
        if (definition.CompletedState is null || definition.FailedState is null || definition.CompensatingState is null || definition.CompensatedState is null)
            throw new InvalidOperationException($"Saga '{definition.SagaType}' must explicitly register all terminal state names.");
        if (definition.Messages.Count == 0) throw new InvalidOperationException($"Saga '{definition.SagaType}' must register at least one message contract.");
        if (!definition.Messages.Values.Any(x => x.Start is not null)) throw new InvalidOperationException($"Saga '{definition.SagaType}' must register at least one legal starter.");
        foreach (SagaMessageRegistration message in definition.Messages.Values)
        {
            if (message.Continuation?.MissingPolicy == SagaMissingInstancePolicy.StartIfAllowed && message.Start is null)
                throw new InvalidOperationException($"Saga message '{message.MessageName}' uses StartIfAllowed but is not registered as a legal starter.");
        }
        ValidateMigrationGraph(definition);
    }

    private static void ValidateMigrationGraph(SagaDefinitionRegistration definition)
    {
        foreach (SagaMigratorRegistration edge in definition.Migrators.Values)
        {
            if (edge.FromVersion <= 0 || edge.ToVersion <= edge.FromVersion || edge.ToVersion > definition.StateSchemaVersion)
                throw new InvalidOperationException($"Saga '{definition.SagaType}' has invalid state migration edge {edge.FromVersion}->{edge.ToVersion}.");
        }
        foreach (int start in definition.Migrators.Keys)
        {
            int version = start;
            var seen = new HashSet<int>();
            while (version < definition.StateSchemaVersion)
            {
                if (!seen.Add(version) || !definition.Migrators.TryGetValue(version, out SagaMigratorRegistration? edge))
                    throw new InvalidOperationException($"Saga '{definition.SagaType}' has an incomplete or cyclic state migration graph from schema v{start}.");
                version = edge.ToVersion;
            }
            if (version != definition.StateSchemaVersion)
                throw new InvalidOperationException($"Saga '{definition.SagaType}' migration graph from schema v{start} does not terminate at current v{definition.StateSchemaVersion}.");
        }
    }

    private static void ValidateDefinitionVersion(SagaInstance instance, SagaDefinitionRegistration definition)
    {
        if (instance.DefinitionVersion == definition.DefinitionVersion) return;
        if (instance.DefinitionVersion <= 0 || instance.DefinitionVersion > definition.DefinitionVersion || !definition.SupportedDefinitionVersions.Contains(instance.DefinitionVersion))
            throw new SagaValidationException("UnsupportedDefinitionVersion");
    }

    private static void EnsurePayloadSize(string payload, TcjSagaOptions options)
    {
        if (Encoding.UTF8.GetByteCount(payload) > options.MaximumStatePayloadBytes)
            throw new SagaValidationException("StatePayloadTooLarge");
    }

    private static async Task CancelAllActiveTimersAsync<TDbContext>(TDbContext dbContext, Guid sagaId, DateTimeOffset now, CancellationToken cancellationToken) where TDbContext : DbContext
    {
        SagaTimer[] timers = await dbContext.Set<SagaTimer>()
            .Where(x => x.SagaId == sagaId && x.Status == SagaTimerStatus.Scheduled)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (SagaTimer timer in timers)
        {
            timer.Status = SagaTimerStatus.Canceled;
            timer.LockId = null;
            timer.LockedAtUtc = null;
            timer.LockExpiresAtUtc = null;
            timer.UpdatedAtUtc = now;
        }
    }

    private static async Task RetireActiveCorrelationsAsync<TDbContext>(TDbContext dbContext, Guid sagaId, DateTimeOffset now, CancellationToken cancellationToken) where TDbContext : DbContext
    {
        SagaCorrelation[] correlations = await dbContext.Set<SagaCorrelation>()
            .Where(x => x.SagaId == sagaId && x.RemovedAtUtc == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (SagaCorrelation correlation in correlations) correlation.RemovedAtUtc = now;
    }
}
