namespace TCJ.Core.Outbox;

/// <summary>
/// Safe delivery metadata made available while a committed outbox event is dispatched.
/// </summary>
/// <param name="MessageId">Stable outbox message identifier.</param>
/// <param name="EventType">Stable logical event-type name.</param>
/// <param name="Attempt">One-based delivery attempt number.</param>
public sealed record OutboxMessageContext(Guid MessageId, string EventType, int Attempt)
{
    /// <summary>Creates delivery metadata with optional correlation and causation identifiers.</summary>
    /// <param name="messageId">Stable outbox message identifier.</param>
    /// <param name="eventType">Stable logical event-type name.</param>
    /// <param name="attempt">One-based delivery attempt number.</param>
    /// <param name="correlationId">Optional correlation identifier propagated from the inbound Inbox context.</param>
    /// <param name="causationId">Optional causation identifier; Inbox-originated messages use the inbound message ID.</param>
    public OutboxMessageContext(Guid messageId, string eventType, int attempt, string? correlationId, string? causationId)
        : this(messageId, eventType, attempt)
    {
        CorrelationId = correlationId;
        CausationId = causationId;
    }

    /// <summary>Gets the optional correlation identifier propagated from the persisted Outbox message.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Gets the optional causation identifier propagated from the persisted Outbox message.</summary>
    public string? CausationId { get; init; }
}
