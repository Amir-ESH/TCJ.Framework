using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TCJ.Core.Identifiers;
using TCJ.Core.Inbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.Messaging.Sagas.Configuration;
using TCJ.Messaging.Sagas.Diagnostics;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

internal sealed class SagaInboxMessageHandler<TDbContext, TMessage> : IInboxMessageHandler<TMessage>
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    private readonly TDbContext _dbContext;
    private readonly SagaDefinitionRegistry _registry;
    private readonly ISagaProviderStorage _providerStorage;
    private readonly TcjSagaOptions _options;
    private readonly IGuidGenerator _guidGenerator;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceProvider _services;

    public SagaInboxMessageHandler(
        TDbContext dbContext,
        SagaDefinitionRegistry registry,
        ISagaProviderStorage providerStorage,
        TcjSagaOptions options,
        IGuidGenerator guidGenerator,
        TimeProvider timeProvider,
        IServiceProvider services)
    {
        _dbContext = dbContext;
        _registry = registry;
        _providerStorage = providerStorage;
        _options = options;
        _guidGenerator = guidGenerator;
        _timeProvider = timeProvider;
        _services = services;
    }

    public async Task HandleAsync(TMessage message, InboxMessageContext inboxContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        (SagaDefinitionRegistration definition, SagaMessageRegistration registration) = _registry.GetByMessage(typeof(TMessage));
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = SagaDiagnostics.Start(
            TcjSagaDiagnosticNames.Activities.Handle,
            definition.SagaType,
            definition.DefinitionVersion,
            "message",
            registration.MessageName);
        try
        {
            bool handled = registration.Continuation is not null
                ? await TryContinueAsync(message!, inboxContext, definition, registration, cancellationToken).ConfigureAwait(false)
                : false;
            if (!handled)
            {
                if (registration.Start is null) throw new SagaValidationException("MessageHasNoStartOrContinuation");
                await StartAsync(message!, inboxContext, definition, registration.Start, cancellationToken).ConfigureAwait(false);
            }
            SagaDiagnostics.Complete(activity, definition.SagaType, definition.DefinitionVersion, "message", "success", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (SagaIgnoreSignal)
        {
            SagaDiagnostics.Complete(activity, definition.SagaType, definition.DefinitionVersion, "message", "ignored", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception exception)
        {
            SagaDiagnostics.Complete(activity, definition.SagaType, definition.DefinitionVersion, "message", "failure", Stopwatch.GetElapsedTime(started).TotalMilliseconds, exception.GetType().Name);
            throw;
        }
    }

    private async Task<bool> TryContinueAsync(
        object message,
        InboxMessageContext inboxContext,
        SagaDefinitionRegistration definition,
        SagaMessageRegistration messageRegistration,
        CancellationToken cancellationToken)
    {
        SagaContinuationRegistration continuation = messageRegistration.Continuation!;
        string hash = SagaStateRuntime.HashCorrelation(continuation.Correlate(message));
        SagaCorrelation? correlation = await _dbContext.Set<SagaCorrelation>().AsNoTracking()
            .Where(x => x.SagaType == definition.SagaType && x.CorrelationName == continuation.CorrelationName && x.ValueHash == hash)
            .OrderBy(x => x.RemovedAtUtc == null ? 0 : 1)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        Guid? sagaId = correlation?.SagaId;

        if (!sagaId.HasValue)
        {
            if (continuation.MissingPolicy == SagaMissingInstancePolicy.StartIfAllowed && messageRegistration.Start is not null) return false;
            ApplyMissingPolicy(continuation.MissingPolicy);
            return true;
        }

        SagaInstance instance = await _dbContext.Set<SagaInstance>().SingleOrDefaultAsync(x => x.SagaId == sagaId.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new SagaRetryException("CorrelationWithoutSaga");

        if (instance.Status is SagaStatus.Completed or SagaStatus.Failed or SagaStatus.Compensated)
        {
            if (!continuation.AllowTerminal)
            {
                if (continuation.MissingPolicy == SagaMissingInstancePolicy.StartIfAllowed && messageRegistration.Start is not null) return false;
                ApplyInvalidPolicy(continuation.InvalidTransitionPolicy, "TerminalSaga");
            }
        }
        else if (!continuation.AllowedStates.Contains(instance.CurrentState))
        {
            ApplyInvalidPolicy(continuation.InvalidTransitionPolicy, "OutOfOrderState");
        }

        object state = await SagaStateRuntime.LoadStateAsync(instance, definition, _services, _options, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var context = CreateContext(instance, definition, inboxContext, messageRegistration, now);
        await continuation.Invoke(_services, state, message, context, cancellationToken).ConfigureAwait(false);
        await SagaStateRuntime.ApplyContextAsync(_dbContext, instance, definition, context, state, _options, now, requireExplicitLifecycle: true, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task StartAsync(
        object message,
        InboxMessageContext inboxContext,
        SagaDefinitionRegistration definition,
        SagaStartRegistration start,
        CancellationToken cancellationToken)
    {
        string hash = SagaStateRuntime.HashCorrelation(start.Correlate(message));
        bool exists = await _dbContext.Set<SagaCorrelation>().AsNoTracking()
            .AnyAsync(x => x.SagaType == definition.SagaType && x.CorrelationName == start.CorrelationName && x.ValueHash == hash && x.RemovedAtUtc == null, cancellationToken)
            .ConfigureAwait(false);
        if (exists) throw new SagaValidationException("ActiveCorrelationAlreadyExists");

        DateTimeOffset now = _timeProvider.GetUtcNow();
        Guid sagaId = _guidGenerator.CreateVersion7();
        var correlation = new SagaCorrelation(sagaId, definition.SagaType, start.CorrelationName, hash, now);
        await _providerStorage.ReserveCorrelationAsync(correlation, cancellationToken).ConfigureAwait(false);

        object state = definition.StateFactory();
        string payload = SagaStateRuntime.SerializeState(state, definition, _options);
        var instance = new SagaInstance(sagaId, definition.SagaType, definition.DefinitionVersion, definition.StateSchemaVersion, start.InitialState, payload, now);
        _dbContext.Set<SagaInstance>().Add(instance);
        var context = new SagaContext(
            sagaId,
            definition.SagaType,
            definition.DefinitionVersion,
            start.InitialState,
            inboxContext.MessageId,
            inboxContext.MessageType,
            inboxContext.MessageVersion,
            inboxContext.Attempt,
            inboxContext.CorrelationId,
            inboxContext.CausationId,
            now);
        await start.Invoke(_services, state, message, context, cancellationToken).ConfigureAwait(false);
        await SagaStateRuntime.ApplyContextAsync(_dbContext, instance, definition, context, state, _options, now, requireExplicitLifecycle: false, cancellationToken).ConfigureAwait(false);
    }

    private static SagaContext CreateContext(
        SagaInstance instance,
        SagaDefinitionRegistration definition,
        InboxMessageContext inboxContext,
        SagaMessageRegistration messageRegistration,
        DateTimeOffset now) =>
        new(
            instance.SagaId,
            definition.SagaType,
            definition.DefinitionVersion,
            instance.CurrentState,
            inboxContext.MessageId,
            inboxContext.MessageType,
            inboxContext.MessageVersion,
            inboxContext.Attempt,
            inboxContext.CorrelationId,
            inboxContext.CausationId,
            now);

    private static void ApplyMissingPolicy(SagaMissingInstancePolicy policy)
    {
        switch (policy)
        {
            case SagaMissingInstancePolicy.Retry: throw new SagaRetryException("MissingSaga");
            case SagaMissingInstancePolicy.DeadLetter: throw new SagaValidationException("MissingSaga");
            case SagaMissingInstancePolicy.Ignore: throw new SagaIgnoreSignal();
            case SagaMissingInstancePolicy.StartIfAllowed: throw new SagaValidationException("StartIfAllowedWithoutStarter");
            default: throw new SagaValidationException("UnknownMissingPolicy");
        }
    }

    internal static void ApplyInvalidPolicy(SagaInvalidTransitionPolicy policy, string reason)
    {
        switch (policy)
        {
            case SagaInvalidTransitionPolicy.Retry: throw new SagaRetryException(reason);
            case SagaInvalidTransitionPolicy.DeadLetter: throw new SagaValidationException(reason);
            case SagaInvalidTransitionPolicy.Ignore: throw new SagaIgnoreSignal();
            default: throw new SagaValidationException("UnknownInvalidTransitionPolicy");
        }
    }

    private sealed class SagaIgnoreSignal : Exception;
}
