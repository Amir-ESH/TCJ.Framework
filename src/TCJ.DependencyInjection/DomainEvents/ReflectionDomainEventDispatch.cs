using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;

namespace TCJ.DependencyInjection.DomainEvents;

internal static class ReflectionDomainEventDispatch
{
    private const string RequiresUnreferencedCodeMessage =
        "Convention-based domain-event dispatch resolves handlers from runtime event types and is not trimming-safe. " +
        "Use AddTcjDependencyInjection(), AddTcjDomainEvent<TEvent>(), and explicit handler registrations.";

    private const string RequiresDynamicCodeMessage =
        "Convention-based domain-event dispatch closes generic invoker types from runtime event types and is not Native AOT-safe. " +
        "Use AddTcjDependencyInjection(), AddTcjDomainEvent<TEvent>(), and explicit handler registrations.";

    // Stryker disable once all: MTP 4.16 reuses the test host; mutating this process-wide cache can contaminate later mutant sessions.
    private static readonly ConcurrentDictionary<Type, ObjectFactory> InvokerFactories = new();

    [RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    [RequiresDynamicCode(RequiresDynamicCodeMessage)]
    internal static Task DispatchAsync(
        IServiceProvider serviceProvider,
        IDomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(domainEvent);

        Type eventType = domainEvent.GetType();
        ObjectFactory factory = InvokerFactories.GetOrAdd(
            eventType,
            static type =>
            {
                Type invokerType = typeof(DomainEventHandlerInvoker<>)
                    .MakeGenericType(type);

                return ActivatorUtilities.CreateFactory(
                    invokerType,
                    [typeof(IServiceProvider)]);
            });

        var invoker = (IDomainEventHandlerInvoker)factory(
            serviceProvider,
            [serviceProvider]);

        return invoker.InvokeAsync(domainEvent, cancellationToken);
    }
}
