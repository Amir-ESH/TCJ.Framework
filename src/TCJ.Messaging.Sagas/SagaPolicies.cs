namespace TCJ.Messaging.Sagas;

/// <summary>Bounded behavior when a continuation cannot find its correlated Saga instance.</summary>
public enum SagaMissingInstancePolicy
{
    /// <summary>Ask the existing Inbox retry semantics to redeliver later.</summary>
    Retry = 0,
    /// <summary>Treat the message as permanently unprocessable.</summary>
    DeadLetter = 1,
    /// <summary>Consume the message without mutating Saga state.</summary>
    Ignore = 2,
    /// <summary>Start only when this exact message contract is explicitly registered as a legal starter.</summary>
    StartIfAllowed = 3
}

/// <summary>Bounded behavior when a valid Saga is not in a state allowed by the incoming transition.</summary>
public enum SagaInvalidTransitionPolicy
{
    /// <summary>Ask the existing Inbox retry semantics to redeliver later.</summary>
    Retry = 0,
    /// <summary>Treat the transition as permanently invalid.</summary>
    DeadLetter = 1,
    /// <summary>Consume the message without mutating Saga state.</summary>
    Ignore = 2
}
