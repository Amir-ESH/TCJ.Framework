using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TCJ.Core.DomainEvents;
using TCJ.Core.Identifiers;
using TCJ.DependencyInjection.Diagnostics;
using TCJ.DependencyInjection.DomainEvents;
using TCJ.DependencyInjection.Lifetimes;
using TCJ.DependencyInjection.Registration;

namespace TCJ.DependencyInjection.Extensions;

/// <summary>
/// Registers TCJ framework services and convention-based application dependencies.
/// </summary>
public static class ServiceCollectionExtensions
{
    private const string ConventionScanningRequiresUnreferencedCodeMessage =
        "Convention-based dependency scanning uses runtime reflection over consumer assemblies and is not trimming-safe. " +
        "Use AddTcjDependencyInjection(), AddTcjDomainEvent<TEvent>(), and explicit application registrations.";

    private const string ConventionScanningRequiresDynamicCodeMessage =
        "Convention-based dependency scanning enables runtime generic domain-event dispatch and is not Native AOT-safe. " +
        "Use AddTcjDependencyInjection(), AddTcjDomainEvent<TEvent>(), and explicit application registrations.";

    // Stryker disable once all: MTP 4.16 reuses the test host; mutating this process-wide registration table can contaminate later mutant sessions.
    private static readonly DependencyLifetimeDefinition[] LifetimeDefinitions =
    [
        new(typeof(ITransientDependency), ServiceLifetime.Transient, RegisterAsSelf: false),
        new(typeof(IScopedDependency), ServiceLifetime.Scoped, RegisterAsSelf: false),
        new(typeof(ISingletonDependency), ServiceLifetime.Singleton, RegisterAsSelf: false),

        new(typeof(ISelfTransientDependency), ServiceLifetime.Transient, RegisterAsSelf: true),
        new(typeof(ISelfScopedDependency), ServiceLifetime.Scoped, RegisterAsSelf: true),
        new(typeof(ISelfSingletonDependency), ServiceLifetime.Singleton, RegisterAsSelf: true)
    ];

    /// <summary>
    /// Registers the reflection-free TCJ framework defaults without scanning application assemblies.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection so additional registrations can be chained.</returns>
    /// <remarks>
    /// This is the trimming-safe bootstrap path. Register application services explicitly with
    /// <see cref="IServiceCollection"/> after calling this method. For domain events, also declare
    /// each closed event type with <see cref="AddTcjDomainEvent{TEvent}(IServiceCollection)"/>.
    /// </remarks>
    public static IServiceCollection AddTcjDependencyInjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        lock (services)
        {
            DependencyInjectionTelemetryDiagnostics.ObserveRegistration(
                services,
                () => RegisterFrameworkServices(services));
        }

        return services;
    }

    /// <summary>
    /// Declares an AOT-safe domain-event dispatch route for a closed event type.
    /// </summary>
    /// <typeparam name="TEvent">The domain-event type whose handlers will be resolved explicitly.</typeparam>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection so additional registrations can be chained.</returns>
    /// <remarks>
    /// This method does not discover or register handlers. Register
    /// <c>IDomainEventHandler&lt;TEvent&gt;</c> implementations explicitly with normal
    /// <see cref="IServiceCollection"/> methods. Repeated calls are idempotent.
    /// </remarks>
    public static IServiceCollection AddTcjDomainEvent<TEvent>(this IServiceCollection services)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(services);

        lock (services)
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IDomainEventDispatchRoute, DomainEventDispatchRoute<TEvent>>());
        }

        return services;
    }

    /// <summary>
    /// Registers TCJ framework defaults and scans the supplied assemblies for lifetime markers.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="assemblies">The explicit assemblies to scan.</param>
    /// <remarks>
    /// This overload uses runtime reflection to discover application types. In trimmed or Native AOT
    /// applications, use the parameterless overload and register application services explicitly.
    /// </remarks>
    [RequiresUnreferencedCode(ConventionScanningRequiresUnreferencedCodeMessage)]
    [RequiresDynamicCode(ConventionScanningRequiresDynamicCodeMessage)]
    public static IServiceCollection AddTcjDependencyInjection(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        var options = new TcjDependencyInjectionOptions()
            .AddAssemblies(assemblies);

        return services.AddTcjDependencyInjection(options);
    }

    /// <summary>
    /// Registers TCJ framework defaults and convention-based dependencies using
    /// caller-provided options.
    /// </summary>
    /// <remarks>
    /// This overload can select runtime assembly scanning and is therefore trimming-restricted.
    /// Use the parameterless overload for the reflection-free framework bootstrap.
    /// </remarks>
    [RequiresUnreferencedCode(ConventionScanningRequiresUnreferencedCodeMessage)]
    [RequiresDynamicCode(ConventionScanningRequiresDynamicCodeMessage)]
    public static IServiceCollection AddTcjDependencyInjection(
        this IServiceCollection services,
        Action<TcjDependencyInjectionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TcjDependencyInjectionOptions();
        configure(options);

        return services.AddTcjDependencyInjection(options);
    }

    /// <summary>
    /// Registers TCJ framework defaults and convention-based dependencies using
    /// an existing options instance.
    /// </summary>
    /// <remarks>
    /// This overload can select runtime assembly scanning and is therefore trimming-restricted.
    /// Use the parameterless overload for the reflection-free framework bootstrap.
    /// </remarks>
    [RequiresUnreferencedCode(ConventionScanningRequiresUnreferencedCodeMessage)]
    [RequiresDynamicCode(ConventionScanningRequiresDynamicCodeMessage)]
    public static IServiceCollection AddTcjDependencyInjection(
        this IServiceCollection services,
        TcjDependencyInjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // IServiceCollection is mutable and does not provide a concurrent-write contract.
        // Serialize TCJ-owned registration mutations so concurrent calls to this extension
        // cannot corrupt the collection or bypass TryAdd duplicate protection. External
        // mutation of the same collection must follow the same application-level boundary.
        lock (services)
        {
            DependencyInjectionTelemetryDiagnostics.ObserveRegistration(
                services,
                () =>
                {
                    if (options.RegisterFrameworkServices)
                    {
                        RegisterFrameworkServices(services);
                    }

                    RegisterReflectionDomainEventDispatchRoute(services);

                    Type[] implementationTypes = DependencyInjectionTelemetryDiagnostics.ObserveScan(
                        options.Assemblies.Count,
                        () => options.Assemblies
                            .SelectMany(GetPublicConcreteTypes)
                            .Distinct()
                            .OrderBy(type => type.FullName, StringComparer.Ordinal)
                            .ToArray());

                    if (options.RegisterDomainEventHandlers)
                    {
                        RegisterDomainEventHandlers(services, implementationTypes);
                    }

                    RegisterMarkedDependencies(services, implementationTypes);
                });
        }

        return services;
    }

    [RequiresUnreferencedCode(ConventionScanningRequiresUnreferencedCodeMessage)]
    [RequiresDynamicCode(ConventionScanningRequiresDynamicCodeMessage)]
    private static void RegisterReflectionDomainEventDispatchRoute(IServiceCollection services)
    {
        Func<IServiceProvider, IDomainEvent, CancellationToken, Task> dispatch =
            ReflectionDomainEventDispatch.DispatchAsync;

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDomainEventDispatchRoute>(
                new ReflectionDomainEventDispatchRoute(dispatch)));
    }

    private static void RegisterFrameworkServices(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IGuidGenerator, GuidGenerator>();
        services.TryAddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
    }

    private static void RegisterDomainEventHandlers(
        IServiceCollection services,
        IEnumerable<Type> implementationTypes)
    {
        foreach (var implementationType in implementationTypes)
        {
            foreach (var serviceType in GetDomainEventHandlerInterfaces(implementationType))
            {
                services.TryAddEnumerable(
                    new ServiceDescriptor(
                        serviceType,
                        implementationType,
                        ServiceLifetime.Transient));
            }
        }
    }

    private static void RegisterMarkedDependencies(
        IServiceCollection services,
        IEnumerable<Type> implementationTypes)
    {
        foreach (var implementationType in implementationTypes)
        {
            if (GetDomainEventHandlerInterfaces(implementationType).Any())
            {
                continue;
            }

            var matchingLifetimes = LifetimeDefinitions
                .Where(definition => definition.MarkerType.IsAssignableFrom(implementationType))
                .ToArray();

            if (matchingLifetimes.Length == 0)
            {
                continue;
            }

            if (matchingLifetimes.Length > 1)
            {
                var markers = string.Join(
                    ", ",
                    matchingLifetimes.Select(definition => definition.MarkerType.Name));

                throw new InvalidOperationException(
                    $"Type '{implementationType.FullName}' implements multiple TCJ lifetime markers: {markers}. " +
                    "A dependency must declare exactly one lifetime marker.");
            }

            var lifetime = matchingLifetimes[0];

            if (lifetime.RegisterAsSelf)
            {
                services.TryAdd(
                    new ServiceDescriptor(
                        implementationType,
                        implementationType,
                        lifetime.Lifetime));

                continue;
            }

            var serviceTypes = GetServiceInterfaces(implementationType).ToArray();

            if (serviceTypes.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Type '{implementationType.FullName}' implements '{lifetime.MarkerType.Name}' " +
                    "but does not expose a service interface. Implement a service contract or use the " +
                    "corresponding self-registration marker.");
            }

            foreach (var serviceType in serviceTypes)
            {
                services.TryAdd(
                    new ServiceDescriptor(
                        serviceType,
                        implementationType,
                        lifetime.Lifetime));
            }
        }
    }

    private static IEnumerable<Type> GetServiceInterfaces(Type implementationType) =>
        implementationType
            .GetInterfaces()
            .Where(interfaceType => !typeof(IDependency).IsAssignableFrom(interfaceType))
            .Where(interfaceType => interfaceType != typeof(IDisposable))
            .Where(interfaceType => interfaceType != typeof(IAsyncDisposable))
            .Where(interfaceType => !IsDomainEventHandlerInterface(interfaceType))
            .Select(interfaceType => NormalizeServiceType(interfaceType, implementationType))
            .Distinct();

    private static IEnumerable<Type> GetDomainEventHandlerInterfaces(Type implementationType) =>
        implementationType
            .GetInterfaces()
            .Where(IsDomainEventHandlerInterface)
            .Select(interfaceType => NormalizeServiceType(interfaceType, implementationType))
            .Distinct();

    private static bool IsDomainEventHandlerInterface(Type interfaceType) =>
        interfaceType.IsGenericType &&
        interfaceType.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>);

    private static Type NormalizeServiceType(
        Type serviceType,
        Type implementationType)
    {
        if (implementationType.IsGenericTypeDefinition &&
            serviceType.IsGenericType &&
            serviceType.ContainsGenericParameters)
        {
            return serviceType.GetGenericTypeDefinition();
        }

        return serviceType;
    }

    [RequiresUnreferencedCode(ConventionScanningRequiresUnreferencedCodeMessage)]
    private static IEnumerable<Type> GetPublicConcreteTypes(Assembly assembly)
    {
        try
        {
            return assembly
                .GetTypes()
                .Where(type => type.IsClass && !type.IsAbstract)
                .Where(type => type.IsPublic || type.IsNestedPublic)
                .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false));
        }
        catch (ReflectionTypeLoadException exception)
        {
            var loaderMessages = exception.LoaderExceptions
                .Where(loaderException => loaderException is not null)
                .Select(loaderException => loaderException!.Message)
                .Distinct(StringComparer.Ordinal);

            throw new InvalidOperationException(
                $"Assembly '{assembly.FullName}' could not be scanned. " +
                string.Join(" | ", loaderMessages),
                exception);
        }
    }

    private sealed record DependencyLifetimeDefinition(
        Type MarkerType,
        ServiceLifetime Lifetime,
        bool RegisterAsSelf);
}
