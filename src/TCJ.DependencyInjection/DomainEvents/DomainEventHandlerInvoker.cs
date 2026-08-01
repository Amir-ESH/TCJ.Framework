using TCJ.Core.DomainEvents;

namespace TCJ.DependencyInjection.DomainEvents;

/// <summary>
/// Provides a non-generic dispatch boundary for a runtime domain-event type.
/// </summary>
internal interface IDomainEventHandlerInvoker
{
    Task InvokeAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken);
}

/// <summary>
/// Invokes all handlers registered for <typeparamref name="TEvent"/> in
/// dependency-registration order.
/// </summary>
/// <typeparam name="TEvent">The concrete domain-event type.</typeparam>
internal sealed class DomainEventHandlerInvoker<TEvent>(
    IEnumerable<IDomainEventHandler<TEvent>> handlers)
    : IDomainEventHandlerInvoker
    where TEvent : IDomainEvent
{
    public async Task InvokeAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent is not TEvent typedEvent)
        {
            throw new ArgumentException(
                $"Domain event type '{domainEvent.GetType().FullName}' cannot be handled " +
                $"by an invoker for '{typeof(TEvent).FullName}'.",
                nameof(domainEvent));
        }

        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await handler
                .HandleAsync(typedEvent, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
