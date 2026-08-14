using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.AspNetCore.HealthChecks;
using TCJ.Core.HealthChecks;

namespace TCJ.AspNetCore.Extensions;

/// <summary>Maps stable TCJ liveness, readiness, combined, and protected diagnostic endpoints.</summary>
public static class HealthCheckEndpointRouteBuilderExtensions
{
    private static readonly ConditionalWeakTable<IEndpointRouteBuilder, EndpointRegistry> Registries = new();

    /// <summary>Maps the lightweight TCJ liveness endpoint. The default path is <c>/health/live</c>.</summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="pattern">Endpoint path.</param>
    /// <param name="configure">Optional standard health-check endpoint customization.</param>
    /// <returns>The mapped endpoint convention builder.</returns>
    public static IEndpointConventionBuilder MapTcjLivenessChecks(
        this IEndpointRouteBuilder endpoints,
        string pattern = TcjHealthEndpointDefaults.LivenessPath,
        Action<HealthCheckOptions>? configure = null)
        => MapOnce(endpoints, pattern, "live", CreateOptions(TcjHealthCheckNames.Tags.Live, TcjHealthResponseWriter.WritePublicAsync, configure), requireAuthorization: false);

    /// <summary>Maps the TCJ readiness endpoint. The default path is <c>/health/ready</c>.</summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="pattern">Endpoint path.</param>
    /// <param name="configure">Optional standard health-check endpoint customization.</param>
    /// <returns>The mapped endpoint convention builder.</returns>
    public static IEndpointConventionBuilder MapTcjReadinessChecks(
        this IEndpointRouteBuilder endpoints,
        string pattern = TcjHealthEndpointDefaults.ReadinessPath,
        Action<HealthCheckOptions>? configure = null)
        => MapOnce(endpoints, pattern, "ready", CreateOptions(TcjHealthCheckNames.Tags.Ready, TcjHealthResponseWriter.WritePublicAsync, configure), requireAuthorization: false);

    /// <summary>Maps a combined endpoint containing all TCJ-tagged health checks.</summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="pattern">Endpoint path.</param>
    /// <param name="configure">Optional standard health-check endpoint customization.</param>
    /// <returns>The mapped endpoint convention builder.</returns>
    public static IEndpointConventionBuilder MapTcjHealthChecks(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/health",
        Action<HealthCheckOptions>? configure = null)
        => MapOnce(endpoints, pattern, "all", CreateOptions(TcjHealthCheckNames.Tags.Tcj, TcjHealthResponseWriter.WritePublicAsync, configure), requireAuthorization: false);

    /// <summary>
    /// Maps sanitized per-check diagnostics. Authorization is required by default and consumers may add a specific policy to the returned builder.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="pattern">Endpoint path.</param>
    /// <param name="configure">Optional standard health-check endpoint customization.</param>
    /// <returns>The mapped authorization-protected endpoint convention builder.</returns>
    public static IEndpointConventionBuilder MapTcjHealthDetails(
        this IEndpointRouteBuilder endpoints,
        string pattern = TcjHealthEndpointDefaults.DetailsPath,
        Action<HealthCheckOptions>? configure = null)
        => MapOnce(endpoints, pattern, "details", CreateOptions(TcjHealthCheckNames.Tags.Tcj, TcjHealthResponseWriter.WriteDetailedAsync, configure), requireAuthorization: true);

    private static HealthCheckOptions CreateOptions(
        string requiredTag,
        Func<HttpContext, HealthReport, Task> writer,
        Action<HealthCheckOptions>? configure)
    {
        var options = new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(requiredTag, StringComparer.Ordinal),
            ResponseWriter = writer,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            }
        };
        configure?.Invoke(options);
        return options;
    }

    private static IEndpointConventionBuilder MapOnce(
        IEndpointRouteBuilder endpoints,
        string pattern,
        string kind,
        HealthCheckOptions options,
        bool requireAuthorization)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        EndpointRegistry registry = Registries.GetValue(endpoints, static _ => new EndpointRegistry());
        string key = $"{kind}:{pattern}";
        lock (registry.Sync)
        {
            if (registry.Builders.TryGetValue(key, out IEndpointConventionBuilder? existing))
            {
                return existing;
            }

            IEndpointConventionBuilder builder = endpoints.MapHealthChecks(pattern, options);
            if (requireAuthorization)
            {
                builder.RequireAuthorization();
            }

            registry.Builders.Add(key, builder);
            return builder;
        }
    }

    private sealed class EndpointRegistry
    {
        internal object Sync { get; } = new();
        internal Dictionary<string, IEndpointConventionBuilder> Builders { get; } = new(StringComparer.Ordinal);
    }
}
