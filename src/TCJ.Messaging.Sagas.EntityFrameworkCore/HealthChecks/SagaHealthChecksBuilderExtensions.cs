using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.HealthChecks;

/// <summary>Registers sanitized Saga liveness and readiness checks.</summary>
public static class SagaHealthChecksBuilderExtensions
{
    /// <summary>Adds dependency-independent liveness and configuration/infrastructure readiness checks.</summary>
    /// <param name="builder">Health-check builder.</param>
    /// <returns>The same health-check builder for chaining.</returns>
    public static IHealthChecksBuilder AddTcjSagas(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        IServiceCollection services = builder.Services;
        lock (services)
        {
            if (services.Any(static descriptor => descriptor.ServiceType == typeof(SagaHealthMarker))) return builder;
            services.AddSingleton<SagaHealthMarker>();
            builder.AddCheck<SagaLivenessHealthCheck>("tcj.saga.liveness", HealthStatus.Unhealthy, ["tcj", "live", "saga"]);
            builder.AddCheck<SagaReadinessHealthCheck>("tcj.saga.readiness", HealthStatus.Unhealthy, ["tcj", "ready", "saga", "configuration"]);
        }
        return builder;
    }

    private sealed class SagaHealthMarker;
}
