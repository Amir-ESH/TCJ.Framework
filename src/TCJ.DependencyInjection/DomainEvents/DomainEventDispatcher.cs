using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;

namespace TCJ.DependencyInjection.DomainEvents;

/// <summary>
/// Dispatches each domain event to all handlers registered for its concrete
/// runtime type.
/// </summary>
/// <remarks>
/// Events and their handlers are processed sequentially. Handler order follows
/// dependency-registration order. Dispatch stops immediately when cancellation
/// is requested or a handler throws; the original handler exception is allowed
/// to propagate unchanged.
/// </remarks>
public sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    /// <summary>
    /// Initializes a domain-event dispatcher with the required service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>

    public DomainEventDispatcher(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public async Task DispatchAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var domainEvent in domainEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (domainEvent is null)
            {
                throw new ArgumentException(
                    "The domain-event collection cannot contain null items.",
                    nameof(domainEvents));
            }

            Type eventType = domainEvent.GetType();
            IDomainEventDispatchRoute[] routes = _serviceProvider
                .GetServices<IDomainEventDispatchRoute>()
                .ToArray();

            IDomainEventDispatchRoute? route = routes
                .FirstOrDefault(candidate => candidate.EventType == eventType)
                ?? routes.FirstOrDefault(candidate => candidate.EventType is null);

            if (route is null)
            {
                continue;
            }

            await route
                .InvokeAsync(_serviceProvider, domainEvent, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
