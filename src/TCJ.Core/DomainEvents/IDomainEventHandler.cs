namespace TCJ.Core.DomainEvents;

/// <summary>
/// Handles a specific domain-event type asynchronously.
/// </summary>
/// <typeparam name="TEvent">The domain-event type.</typeparam>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>
    /// Handles the specified domain event.
    /// </summary>
    /// <param name="domainEvent">The domain event to handle.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    Task HandleAsync(
        TEvent domainEvent,
        CancellationToken cancellationToken);
}
