using System.Collections.Concurrent;
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
    // Stryker disable once all: MTP 4.16 reuses the test host; mutating this process-wide cache can contaminate later mutant sessions.
    private static readonly ConcurrentDictionary<Type, ObjectFactory> InvokerFactories = new();

    private readonly IServiceProvider _serviceProvider;

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

            var invoker = CreateInvoker(domainEvent.GetType());

            await invoker
                .InvokeAsync(domainEvent, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private IDomainEventHandlerInvoker CreateInvoker(Type eventType)
    {
        var factory = InvokerFactories.GetOrAdd(
            eventType,
            static type =>
            {
                var invokerType = typeof(DomainEventHandlerInvoker<>)
                    .MakeGenericType(type);

                return ActivatorUtilities.CreateFactory(
                    invokerType,
                    Type.EmptyTypes);
            });

        return (IDomainEventHandlerInvoker)factory(
            _serviceProvider,
            []);
    }
}
