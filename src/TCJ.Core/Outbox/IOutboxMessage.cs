namespace TCJ.Core.Outbox;

/// <summary>
/// Represents a durable transactional-outbox message.
/// </summary>
public interface IOutboxMessage
{
    /// <summary>Gets the stable identifier assigned before persistence.</summary>
    Guid Id { get; }

    /// <summary>Gets the stable logical event-type name.</summary>
    string EventType { get; }

    /// <summary>Gets the serialized event payload.</summary>
    string Payload { get; }

    /// <summary>Gets the UTC instant at which the domain event occurred.</summary>
    DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Gets the number of delivery attempts already recorded.</summary>
    int AttemptCount { get; }
}
