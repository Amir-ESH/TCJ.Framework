using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Diagnostics;

namespace TCJ.DependencyInjection.Extensions;

/// <summary>
/// Provides backend-neutral TCJ telemetry registration and configuration helpers.
/// </summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Enables the TCJ telemetry registration point without adding an exporter or collector.
    /// Calling this method repeatedly is safe and does not add duplicate service descriptors.
    /// </summary>
    /// <param name="services">The service collection used by the application.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddTcjTelemetry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }

    /// <summary>
    /// Configures process-wide TCJ telemetry without registering an exporter or collector.
    /// Calling this method repeatedly is safe and does not add duplicate service descriptors.
    /// </summary>
    /// <param name="services">The service collection used by the application.</param>
    /// <param name="configure">Telemetry configuration applied to production-safe defaults.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddTcjTelemetry(
        this IServiceCollection services,
        Action<TcjTelemetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        TcjTelemetry.Configure(configure);
        return services;
    }
}
