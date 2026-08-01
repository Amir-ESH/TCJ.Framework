namespace TCJ.Core.DomainEvents;

/// <summary>
/// Represents an event raised by the domain model.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// Gets the instant at which the event occurred.
    /// </summary>
    DateTimeOffset OccurredOn { get; }
}

/// <summary>
/// Represents an entity or aggregate that contains pending domain events.
/// </summary>
public interface IHasDomainEvents
{
    /// <summary>
    /// Gets the pending domain events.
    /// </summary>
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    /// <summary>
    /// Clears the pending domain events after successful dispatch.
    /// </summary>
    void ClearDomainEvents();
}
