using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TCJ.Core.DomainEvents;
using TCJ.Core.Identifiers;
using TCJ.Core.Inbox;
using TCJ.EntityFrameworkCore.Outbox.Diagnostics;

namespace TCJ.EntityFrameworkCore.Outbox.Interceptors;

internal sealed class OutboxSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IGuidGenerator _guidGenerator;
    private readonly IOutboxSerializer _serializer;
    private readonly IOutboxEventTypeResolver _eventTypeResolver;
    private readonly OutboxCaptureTracker _captureTracker;
    private readonly TimeProvider _timeProvider;
    private readonly IInboxMessageContextAccessor? _inboxContextAccessor;

    public OutboxSaveChangesInterceptor(
        IGuidGenerator guidGenerator,
        IOutboxSerializer serializer,
        IOutboxEventTypeResolver eventTypeResolver,
        OutboxCaptureTracker captureTracker,
        TimeProvider timeProvider,
        IInboxMessageContextAccessor? inboxContextAccessor = null)
    {
        _guidGenerator = guidGenerator ?? throw new ArgumentNullException(nameof(guidGenerator));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _eventTypeResolver = eventTypeResolver ?? throw new ArgumentNullException(nameof(eventTypeResolver));
        _captureTracker = captureTracker ?? throw new ArgumentNullException(nameof(captureTracker));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _inboxContextAccessor = inboxContextAccessor;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        CompleteSave(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompleteSave(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        MarkFailure(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        MarkFailure(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        MarkFailure(eventData.Context);
        base.SaveChangesCanceled(eventData);
    }

    public override Task SaveChangesCanceledAsync(
        DbContextEventData eventData,
        CancellationToken cancellationToken = default)
    {
        MarkFailure(eventData.Context);
        return base.SaveChangesCanceledAsync(eventData, cancellationToken);
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        IReadOnlyList<IHasDomainEvents> aggregates = context.ChangeTracker
            .Entries()
            .Select(entry => entry.Entity)
            .OfType<IHasDomainEvents>()
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToArray();

        if (aggregates.Count == 0)
        {
            return;
        }

        OutboxCaptureState state = _captureTracker.GetOrCreate(context);
        bool hasExplicitTransaction = context.Database.CurrentTransaction is not null;
        state.AwaitingExplicitCommit |= hasExplicitTransaction;

        if (hasExplicitTransaction)
        {
            foreach (var entry in context.ChangeTracker.Entries()
                         .Where(entry => entry.Entity is not OutboxMessage)
                         .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                state.OriginalStates.TryAdd(entry.Entity, entry.State);
            }
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        foreach (IHasDomainEvents aggregate in aggregates)
        {
            state.Aggregates.Add(aggregate);

            foreach (IDomainEvent domainEvent in aggregate.DomainEvents)
            {
                if (state.Captured.ContainsKey(domainEvent))
                {
                    continue;
                }

                string eventType = _eventTypeResolver.GetName(domainEvent.GetType());
                using Activity? activity = OutboxTelemetryDiagnostics.Start(
                    TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.OutboxPersist,
                    "persist",
                    eventType);
                try
                {
                    string payload = _serializer.Serialize(domainEvent);
                    Guid messageId = _guidGenerator.CreateVersion7();
                    InboxMessageContext? inboxContext = _inboxContextAccessor?.Current;
                    var message = new OutboxMessage(
                        messageId,
                        domainEvent.OccurredOn.ToUniversalTime(),
                        eventType,
                        payload,
                        now,
                        inboxContext?.CorrelationId,
                        inboxContext?.MessageId);

                    context.Set<OutboxMessage>().Add(message);
                    state.Captured.Add(domainEvent, new CapturedOutboxEvent(domainEvent, message));
                    OutboxTelemetryDiagnostics.CompleteSuccess(activity);
                }
                catch (Exception exception)
                {
                    OutboxTelemetryDiagnostics.CompleteFailure(activity, exception);
                    throw;
                }
            }
        }
    }

    private void CompleteSave(DbContext? context)
    {
        if (context is null || !_captureTracker.TryGet(context, out OutboxCaptureState state))
        {
            return;
        }

        foreach (CapturedOutboxEvent captured in state.Captured.Values)
        {
            // Reaching SavedChanges/SavedChangesAsync means this SaveChanges operation succeeded.
            // Persistence must not depend on EF tracking-state transitions (including callers that
            // disable AcceptAllChanges), because explicit transactions are finalized separately.
            captured.Persisted = true;
        }

        if (state.AwaitingExplicitCommit)
        {
            return;
        }

        RecordCommittedPersistence(state);
        ClearCompletedDomainEvents(state);
        _captureTracker.Remove(context);
    }

    private void MarkFailure(DbContext? context)
    {
        if (context is null || !_captureTracker.TryGet(context, out OutboxCaptureState state))
        {
            return;
        }

        if (state.AwaitingExplicitCommit)
        {
            state.HadSaveFailure = true;
        }
    }

    internal static void RecordCommittedPersistence(OutboxCaptureState state)
    {
        int committed = 0;
        foreach (CapturedOutboxEvent captured in state.Captured.Values)
        {
            if (captured.Persisted && !captured.PersistTelemetryRecorded)
            {
                captured.PersistTelemetryRecorded = true;
                committed++;
            }
        }

        OutboxTelemetryDiagnostics.RecordPersisted(committed);
    }

    internal static void ClearCompletedDomainEvents(OutboxCaptureState state)
    {
        foreach (IHasDomainEvents aggregate in state.Aggregates)
        {
            if (aggregate.DomainEvents.All(domainEvent =>
                    state.Captured.TryGetValue(domainEvent, out CapturedOutboxEvent? captured) && captured.Persisted))
            {
                aggregate.ClearDomainEvents();
            }
        }
    }
}
