using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.HealthChecks;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TCJ.EntityFrameworkCore.HealthChecks;

/// <summary>Registers provider-independent Entity Framework Core readiness checks.</summary>
public static class EntityFrameworkCoreHealthChecksBuilderExtensions
{
    /// <summary>Adds a provider-independent readiness check for a TCJ DbContext.</summary>
    /// <typeparam name="TDbContext">Registered TCJ DbContext type.</typeparam>
    /// <param name="builder">Health-check builder.</param>
    /// <returns>The same builder for further composition.</returns>
    public static IHealthChecksBuilder AddTcjEntityFrameworkCore<TDbContext>(this IHealthChecksBuilder builder)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        EnsureInfrastructure(builder.Services);
        IServiceCollection services = builder.Services;
        lock (services)
        {
            if (services.Any(static descriptor => descriptor.ServiceType == typeof(EntityFrameworkMarker<TDbContext>)))
            {
                return builder;
            }

            services.AddSingleton<EntityFrameworkMarker<TDbContext>>();
            builder.AddCheck<TcjEntityFrameworkCoreHealthCheck<TDbContext>>(
                TcjHealthCheckNames.Checks.EntityFrameworkCore,
                failureStatus: HealthStatus.Unhealthy,
                tags: [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Ready, TcjHealthCheckNames.Tags.Dependency, TcjHealthCheckNames.Tags.Database, TcjHealthCheckNames.Tags.Configuration]);
        }

        return builder;
    }

    private static void EnsureInfrastructure(IServiceCollection services)
    {
        lock (services)
        {
            if (!services.Any(static descriptor => descriptor.ServiceType == typeof(TcjHealthCheckOptions)))
            {
                services.AddSingleton(new TcjHealthCheckOptions());
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

    private sealed class EntityFrameworkMarker<TDbContext> where TDbContext : DbContext { public EntityFrameworkMarker() { } }
}
