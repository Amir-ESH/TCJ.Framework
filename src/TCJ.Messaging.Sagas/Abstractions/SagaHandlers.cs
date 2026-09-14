namespace TCJ.Messaging.Sagas;

/// <summary>Marks a Saga implementation that owns the specified application state type.</summary>
/// <typeparam name="TState">Strongly typed Saga state.</typeparam>
public interface ISaga<TState> where TState : class, ISagaState;

/// <summary>Handles a message that is allowed to start a new Saga instance.</summary>
/// <typeparam name="TState">Strongly typed Saga state.</typeparam>
/// <typeparam name="TMessage">Start message contract.</typeparam>
public interface ISagaStartsWith<TState, in TMessage> : ISaga<TState>
    where TState : class, ISagaState
{
    /// <summary>Applies the start transition.</summary>
    /// <param name="state">Strongly typed mutable Saga state.</param>
    /// <param name="message">Incoming start message.</param>
    /// <param name="context">Transition context used for lifecycle, timer, and durable output mutations.</param>
    /// <param name="cancellationToken">Cancellation token for the current Inbox attempt.</param>
    Task HandleAsync(TState state, TMessage message, SagaContext context, CancellationToken cancellationToken = default);
}

/// <summary>Handles a message correlated to an existing Saga instance.</summary>
/// <typeparam name="TState">Strongly typed Saga state.</typeparam>
/// <typeparam name="TMessage">Continuation message contract.</typeparam>
public interface ISagaHandles<TState, in TMessage> : ISaga<TState>
    where TState : class, ISagaState
{
    /// <summary>Applies a continuation transition.</summary>
    /// <param name="state">Strongly typed mutable Saga state.</param>
    /// <param name="message">Correlated continuation message.</param>
    /// <param name="context">Transition context used for lifecycle, timer, and durable output mutations.</param>
    /// <param name="cancellationToken">Cancellation token for the current Inbox attempt.</param>
    Task HandleAsync(TState state, TMessage message, SagaContext context, CancellationToken cancellationToken = default);
}

/// <summary>Handles a durable Saga-owned timeout.</summary>
/// <typeparam name="TState">Strongly typed Saga state.</typeparam>
public interface ISagaHandlesTimeout<TState> : ISaga<TState>
    where TState : class, ISagaState
{
    /// <summary>Applies a timeout transition for a registered timer contract.</summary>
    /// <param name="state">Strongly typed mutable Saga state.</param>
    /// <param name="timeout">Claimed durable timeout metadata.</param>
    /// <param name="context">Transition context used for lifecycle, timer, and durable output mutations.</param>
    /// <param name="cancellationToken">Cancellation token for the timer-processing attempt.</param>
    Task HandleTimeoutAsync(TState state, SagaTimeout timeout, SagaContext context, CancellationToken cancellationToken = default);
}

/// <summary>Performs explicit application-defined compensation for a Saga.</summary>
/// <typeparam name="TState">Strongly typed Saga state.</typeparam>
public interface ISagaCompensates<TState> : ISaga<TState>
    where TState : class, ISagaState
{
    /// <summary>
    /// Performs the next compensation step. Implementations should persist local changes and emit
    /// remote compensation commands/events through the durable Outbox-compatible path.
    /// </summary>
    /// <param name="state">Strongly typed Saga state being compensated.</param>
    /// <param name="context">Compensation context used to emit durable work and explicitly complete compensation.</param>
    /// <param name="cancellationToken">Cancellation token for the remediation attempt.</param>
    Task CompensateAsync(TState state, SagaContext context, CancellationToken cancellationToken = default);
}
