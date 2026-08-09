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
    /// Registers TCJ framework defaults and scans the supplied assemblies for lifetime markers.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="assemblies">The explicit assemblies to scan.</param>
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
