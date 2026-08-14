using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.HealthChecks;
using TCJ.Core.Outbox;

namespace TCJ.EntityFrameworkCore.Outbox.HealthChecks;

/// <summary>Registers safe transactional-outbox readiness checks.</summary>
public static class OutboxHealthChecksBuilderExtensions
{
    /// <summary>Adds processor, backlog, and dead-letter checks without exposing payloads.</summary>
    /// <param name="builder">Health-check builder to configure.</param>
    /// <returns>The same health-check builder for chaining.</returns>
    public static IHealthChecksBuilder AddTcjOutbox(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        IServiceCollection services = builder.Services;
        lock (services)
        {
            if (services.Any(static descriptor => descriptor.ServiceType == typeof(OutboxHealthMarker)))
            {
                return builder;
            }

            services.AddSingleton<OutboxHealthMarker>();
            builder.AddCheck<OutboxProcessorHealthCheck>(
                TcjHealthCheckNames.Checks.OutboxProcessor,
                failureStatus: HealthStatus.Unhealthy,
                tags: [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Ready, TcjHealthCheckNames.Tags.Outbox]);
            builder.AddCheck<OutboxBacklogHealthCheck>(
                TcjHealthCheckNames.Checks.OutboxBacklog,
                failureStatus: HealthStatus.Unhealthy,
                tags: [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Ready, TcjHealthCheckNames.Tags.Outbox]);
            builder.AddCheck<OutboxDeadLettersHealthCheck>(
                TcjHealthCheckNames.Checks.OutboxDeadLetters,
                failureStatus: HealthStatus.Degraded,
                tags: [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Ready, TcjHealthCheckNames.Tags.Outbox]);
        }

        return builder;
    }

    private sealed class OutboxHealthMarker { }
}
