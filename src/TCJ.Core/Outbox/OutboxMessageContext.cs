namespace TCJ.Core.Outbox;

/// <summary>
/// Safe delivery metadata made available while a committed outbox event is dispatched.
/// </summary>
/// <param name="MessageId">Stable outbox message identifier.</param>
/// <param name="EventType">Stable logical event-type name.</param>
/// <param name="Attempt">One-based delivery attempt number.</param>
public sealed record OutboxMessageContext(Guid MessageId, string EventType, int Attempt);
