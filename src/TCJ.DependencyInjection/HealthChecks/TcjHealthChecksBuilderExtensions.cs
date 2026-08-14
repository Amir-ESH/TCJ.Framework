using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.HealthChecks;

namespace TCJ.DependencyInjection.HealthChecks;

/// <summary>Registers TCJ in-process health checks using the standard .NET health-check infrastructure.</summary>
public static class TcjHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds TCJ health-check infrastructure plus lightweight core liveness and startup readiness checks.
    /// Repeated calls are idempotent; the first options configuration wins.
    /// </summary>
    /// <param name="services">Application service collection.</param>
    /// <param name="configure">Optional bounded health-check configuration.</param>
    /// <returns>The standard health-check builder for further composition.</returns>
    public static IHealthChecksBuilder AddTcjHealthChecks(
        this IServiceCollection services,
        Action<TcjHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = services.AddHealthChecks();
        EnsureInfrastructure(builder, configure);
        AddTcjCore(builder);
        return builder;
    }

    /// <summary>Adds the lightweight TCJ core liveness and startup readiness checks.</summary>
    /// <param name="builder">Health-check builder.</param>
    /// <returns>The same builder for further composition.</returns>
    public static IHealthChecksBuilder AddTcjCore(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        EnsureInfrastructure(builder, configure: null);
        AddOnce<TcjCoreHealthCheck, CoreMarker>(
            builder,
            TcjHealthCheckNames.Checks.Core,
            [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Live]);
        AddOnce<TcjStartupHealthCheck, StartupMarker>(
            builder,
            TcjHealthCheckNames.Checks.Startup,
            [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Ready, TcjHealthCheckNames.Tags.Startup, TcjHealthCheckNames.Tags.Configuration]);
        return builder;
    }

    /// <summary>Adds a readiness check for TCJ dependency-injection framework registrations.</summary>
    /// <param name="builder">Health-check builder.</param>
    /// <returns>The same builder for further composition.</returns>
    public static IHealthChecksBuilder AddTcjDependencyInjection(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        EnsureInfrastructure(builder, configure: null);
        AddOnce<TcjDependencyInjectionHealthCheck, DependencyInjectionMarker>(
            builder,
            TcjHealthCheckNames.Checks.DependencyInjection,
            [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Ready, TcjHealthCheckNames.Tags.Dependency, TcjHealthCheckNames.Tags.Configuration]);
        return builder;
    }

    /// <summary>Adds a readiness check for TCJ domain-event dispatcher infrastructure.</summary>
    /// <param name="builder">Health-check builder.</param>
    /// <returns>The same builder for further composition.</returns>
    public static IHealthChecksBuilder AddTcjDomainEvents(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        EnsureInfrastructure(builder, configure: null);
        AddOnce<TcjDomainEventsHealthCheck, DomainEventsMarker>(
            builder,
            TcjHealthCheckNames.Checks.DomainEvents,
            [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Ready, TcjHealthCheckNames.Tags.Dependency]);
        return builder;
    }

    private static void EnsureInfrastructure(IHealthChecksBuilder builder, Action<TcjHealthCheckOptions>? configure)
    {
        IServiceCollection services = builder.Services;
        lock (services)
        {
            if (!services.Any(static descriptor => descriptor.ServiceType == typeof(TcjHealthCheckOptions)))
            {
                var options = new TcjHealthCheckOptions();
                configure?.Invoke(options);
                options.Validate();
                services.AddSingleton(options);
            }

            if (!services.Any(static descriptor => descriptor.ServiceType == typeof(TcjStartupDiagnostics)))
            {
                services.AddSingleton<TcjStartupDiagnostics>();
            }

            if (!services.Any(static descriptor => descriptor.ServiceType == typeof(TimeProvider)))
            {
                services.AddSingleton(TimeProvider.System);
            }
        }
    }

    private static void AddOnce<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCheck,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMarker>(
        IHealthChecksBuilder builder,
        string name,
        IEnumerable<string> tags)
        where TCheck : class, IHealthCheck
        where TMarker : class
    {
        IServiceCollection services = builder.Services;
        lock (services)
        {
            if (services.Any(static descriptor => descriptor.ServiceType == typeof(TMarker)))
            {
                return;
            }

            services.AddSingleton<TMarker>();
            builder.AddCheck<TCheck>(name, failureStatus: HealthStatus.Unhealthy, tags: tags);
        }
    }

    private sealed class CoreMarker { public CoreMarker() { } }
    private sealed class StartupMarker { public StartupMarker() { } }
    private sealed class DependencyInjectionMarker { public DependencyInjectionMarker() { } }
    private sealed class DomainEventsMarker { public DomainEventsMarker() { } }
}
