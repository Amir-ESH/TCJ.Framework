using TCJ.Core.DomainEvents;

namespace TCJ.Messaging.Sagas;

/// <summary>
/// Supplies stable Saga metadata and explicit durable transition operations to one handler invocation.
/// Saga payloads and correlation values are intentionally not exposed through diagnostic APIs.
/// </summary>
public sealed class SagaContext
{
    private readonly List<SagaTimerMutation> _timerMutations = [];
    private readonly List<IDomainEvent> _outgoingEvents = [];
    private SagaLifecycleAction _lifecycleAction;
    private string? _nextState;

    internal SagaContext(
        Guid sagaId,
        string sagaType,
        int definitionVersion,
        string currentState,
        string incomingMessageId,
        string incomingMessageType,
        int incomingMessageVersion,
        int attempt,
        string? correlationId,
        string? causationId,
        DateTimeOffset utcNow)
    {
        SagaId = sagaId;
        SagaType = sagaType;
        DefinitionVersion = definitionVersion;
        CurrentState = currentState;
        IncomingMessageId = incomingMessageId;
        IncomingMessageType = incomingMessageType;
        IncomingMessageVersion = incomingMessageVersion;
        Attempt = attempt;
        CorrelationId = correlationId;
        CausationId = causationId;
        UtcNow = utcNow.ToUniversalTime();
    }

    /// <summary>Stable framework-generated Saga identity.</summary>
    public Guid SagaId { get; }
    /// <summary>Stable logical Saga contract name.</summary>
    public string SagaType { get; }
    /// <summary>Current Saga definition version.</summary>
    public int DefinitionVersion { get; }
    /// <summary>State at the beginning of this transition.</summary>
    public string CurrentState { get; }
    /// <summary>Stable incoming message identity. Timer/administrative transitions use a framework-owned bounded value.</summary>
    public string IncomingMessageId { get; }
    /// <summary>Stable logical incoming message or timeout contract.</summary>
    public string IncomingMessageType { get; }
    /// <summary>Incoming message schema version, or zero for non-message transitions.</summary>
    public int IncomingMessageVersion { get; }
    /// <summary>One-based bounded processing attempt.</summary>
    public int Attempt { get; }
    /// <summary>Ambient transport correlation identity when supplied by the existing Inbox boundary.</summary>
    public string? CorrelationId { get; }
    /// <summary>Ambient transport causation identity when supplied by the existing Inbox boundary.</summary>
    public string? CausationId { get; }
    /// <summary>Framework time captured for this transition.</summary>
    public DateTimeOffset UtcNow { get; }

    /// <summary>Explicitly keeps the current state while allowing other durable mutations.</summary>
    public void Stay()
    {
        EnsureNoLifecycleAction();
        _lifecycleAction = SagaLifecycleAction.Stay;
        _nextState = CurrentState;
    }

    /// <summary>Transitions the Saga to a stable non-terminal state name.</summary>
    /// <param name="state">Stable target state name.</param>
    public void TransitionTo(string state)
    {
        ValidateState(state);
        EnsureNoLifecycleAction();
        _lifecycleAction = SagaLifecycleAction.Transition;
        _nextState = state;
    }

    /// <summary>Marks the Saga completed. Completion is terminal.</summary>
    public void Complete()
    {
        EnsureNoLifecycleAction();
        _lifecycleAction = SagaLifecycleAction.Complete;
    }

    /// <summary>Marks the Saga failed. Failure is terminal and does not imply remote rollback.</summary>
    public void Fail()
    {
        EnsureNoLifecycleAction();
        _lifecycleAction = SagaLifecycleAction.Fail;
    }

    /// <summary>Requests explicit application-defined compensation.</summary>
    public void RequestCompensation()
    {
        EnsureNoLifecycleAction();
        _lifecycleAction = SagaLifecycleAction.RequestCompensation;
    }

    /// <summary>Marks an explicit application-defined compensation action complete. This is a new business action, not rollback.</summary>
    public void CompleteCompensation()
    {
        EnsureNoLifecycleAction();
        _lifecycleAction = SagaLifecycleAction.CompleteCompensation;
    }

    /// <summary>Schedules or replaces a registered Saga-owned durable deadline.</summary>
    /// <param name="timerName">Registered stable Saga timer name.</param>
    /// <param name="dueAtUtc">UTC deadline, which must be later than the transition time.</param>
    public void ScheduleTimer(string timerName, DateTimeOffset dueAtUtc)
    {
        ValidateTimerName(timerName);
        DateTimeOffset due = dueAtUtc.ToUniversalTime();
        if (due <= UtcNow) throw new ArgumentOutOfRangeException(nameof(dueAtUtc), "Saga timer deadline must be later than the transition time.");
        _timerMutations.Add(new SagaTimerMutation(SagaTimerMutationKind.Schedule, timerName, due));
    }

    /// <summary>Cancels a registered Saga-owned durable deadline if it is currently active.</summary>
    /// <param name="timerName">Registered stable Saga timer name.</param>
    public void CancelTimer(string timerName)
    {
        ValidateTimerName(timerName);
        _timerMutations.Add(new SagaTimerMutation(SagaTimerMutationKind.Cancel, timerName, null));
    }

    /// <summary>
    /// Emits an Outbox-compatible domain/integration event. The EF integration attaches the event
    /// to the tracked Saga instance so the existing Outbox interceptor persists it in the same transaction.
    /// </summary>
    /// <param name="domainEvent">Explicitly registered Outbox-compatible domain or integration event.</param>
    public void Emit(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _outgoingEvents.Add(domainEvent);
    }

    internal SagaLifecycleAction LifecycleAction => _lifecycleAction;
    internal string? NextState => _nextState;
    internal IReadOnlyList<SagaTimerMutation> TimerMutations => _timerMutations;
    internal IReadOnlyList<IDomainEvent> OutgoingEvents => _outgoingEvents;

    private void EnsureNoLifecycleAction()
    {
        if (_lifecycleAction != SagaLifecycleAction.None)
            throw new InvalidOperationException("Only one lifecycle action can be selected for a Saga transition.");
    }

    private static void ValidateState(string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        if (state.Length > 64) throw new ArgumentOutOfRangeException(nameof(state), "Saga state names cannot exceed 64 characters.");
    }

    private static void ValidateTimerName(string timerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timerName);
        if (timerName.Length > 128) throw new ArgumentOutOfRangeException(nameof(timerName), "Saga timer names cannot exceed 128 characters.");
    }
}

internal enum SagaLifecycleAction { None = 0, Stay = 1, Transition = 2, Complete = 3, Fail = 4, RequestCompensation = 5, CompleteCompensation = 6 }
internal enum SagaTimerMutationKind { Schedule = 0, Cancel = 1 }
internal sealed record SagaTimerMutation(SagaTimerMutationKind Kind, string TimerName, DateTimeOffset? DueAtUtc);
