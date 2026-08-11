using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;

namespace TCJ.DependencyInjection.DomainEvents;

/// <summary>
/// Represents a pre-declared domain-event dispatch route. A null event type is reserved
/// for the reflection-based convention-scanning fallback.
/// </summary>
internal interface IDomainEventDispatchRoute
{
    Type? EventType { get; }

    Task InvokeAsync(
        IServiceProvider serviceProvider,
        IDomainEvent domainEvent,
        CancellationToken cancellationToken);
}

/// <summary>
/// AOT-safe closed generic route created explicitly by application registration.
/// </summary>
internal sealed class DomainEventDispatchRoute<TEvent> : IDomainEventDispatchRoute
    where TEvent : IDomainEvent
{
    public Type EventType => typeof(TEvent);

    public Task InvokeAsync(
        IServiceProvider serviceProvider,
        IDomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(domainEvent);

        IEnumerable<IDomainEventHandler<TEvent>> handlers =
            serviceProvider.GetServices<IDomainEventHandler<TEvent>>();
        var invoker = new DomainEventHandlerInvoker<TEvent>(handlers, serviceProvider);
        return invoker.InvokeAsync(domainEvent, cancellationToken);
    }
}

/// <summary>
/// Stores the convention-scanning fallback behind a delegate whose restricted target is
/// created only from the annotated scanning registration path.
/// </summary>
internal sealed class ReflectionDomainEventDispatchRoute(
    Func<IServiceProvider, IDomainEvent, CancellationToken, Task> dispatch)
    : IDomainEventDispatchRoute
{
    private readonly Func<IServiceProvider, IDomainEvent, CancellationToken, Task> _dispatch =
        dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    public Type? EventType => null;

    public Task InvokeAsync(
        IServiceProvider serviceProvider,
        IDomainEvent domainEvent,
        CancellationToken cancellationToken) =>
        _dispatch(serviceProvider, domainEvent, cancellationToken);
}
