using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.HealthChecks;

namespace TCJ.EntityFrameworkCore.Inbox.HealthChecks;

/// <summary>Registers safe transactional Inbox readiness checks.</summary>
public static class InboxHealthChecksBuilderExtensions
{
    /// <summary>Adds configuration, processor, backlog, and dead-letter readiness checks.</summary>
    /// <param name="builder">Health-check builder.</param>
    /// <returns>The same builder for chaining.</returns>
    public static IHealthChecksBuilder AddTcjInbox(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        IServiceCollection services = builder.Services;
        lock (services)
        {
            if (services.Any(static d => d.ServiceType == typeof(InboxHealthMarker))) return builder;
            services.AddSingleton<InboxHealthMarker>();
            string[] tags = [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Ready, TcjHealthCheckNames.Tags.Inbox];
            string[] configurationTags = [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Ready, TcjHealthCheckNames.Tags.Inbox, TcjHealthCheckNames.Tags.Configuration];
            builder.AddCheck<InboxConfigurationHealthCheck>(TcjHealthCheckNames.Checks.InboxConfiguration, HealthStatus.Unhealthy, configurationTags);
            builder.AddCheck<InboxProcessorHealthCheck>(TcjHealthCheckNames.Checks.InboxProcessor, HealthStatus.Unhealthy, tags);
            builder.AddCheck<InboxBacklogHealthCheck>(TcjHealthCheckNames.Checks.InboxBacklog, HealthStatus.Unhealthy, tags);
            builder.AddCheck<InboxDeadLettersHealthCheck>(TcjHealthCheckNames.Checks.InboxDeadLetters, HealthStatus.Degraded, tags);
        }
        return builder;
    }
    private sealed class InboxHealthMarker { }
}
