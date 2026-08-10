using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TCJ.Core.Resilience;
using TCJ.DependencyInjection.DomainEvents;

namespace TCJ.DependencyInjection.Extensions;

/// <summary>
/// Registers optional backend-neutral TCJ resilience policies.
/// Registration alone does not wrap arbitrary application operations.
/// </summary>
public static class ResilienceServiceCollectionExtensions
{
    /// <summary>
    /// Registers explicit retry, timeout, circuit-breaker, and transient-failure services
    /// using production-safe bounded defaults.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjResilience(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddTcjResilience(static _ => { });
    }

    /// <summary>
    /// Registers explicit TCJ resilience services using validated options.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configure">Callback that configures bounded resilience defaults.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjResilience(
        this IServiceCollection services,
        Action<TcjResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TcjResilienceOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ITransientFailureDetector>(serviceProvider =>
            new TransientFailureDetector(serviceProvider.GetServices<ITransientFailureClassifier>()));
        services.TryAddSingleton(serviceProvider => new TcjRetryPolicy(
            serviceProvider.GetRequiredService<ITransientFailureDetector>(),
            options.Retry,
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(serviceProvider => new TcjTimeoutPolicy(
            options.Timeout,
            serviceProvider.GetRequiredService<TimeProvider>()));

        // Circuit breakers are stateful. Transient registration prevents unrelated
        // consumers from accidentally sharing one global circuit state.
        services.TryAddTransient(serviceProvider => new TcjCircuitBreaker(
            serviceProvider.GetRequiredService<ITransientFailureDetector>(),
            options.CircuitBreaker,
            serviceProvider.GetRequiredService<TimeProvider>()));

        return services;
    }

    /// <summary>
    /// Explicitly enables transient retries for individual failing domain-event handlers.
    /// Successful earlier handlers are never repeated. The default dispatcher still performs no retries.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configure">Optional callback that explicitly enables and bounds handler retries.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjDomainEventResilience(
        this IServiceCollection services,
        Action<TcjDomainEventResilienceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new TcjDomainEventResilienceOptions();
        configure?.Invoke(options);
        options.Validate();

        // Register shared classifier/time services with their normal operation-level defaults.
        // Handler retry settings remain isolated in TcjDomainEventResilienceOptions.
        services.AddTcjResilience();
        services.TryAddSingleton(options);
        return services;
    }
}
