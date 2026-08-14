using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.HealthChecks;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TCJ.EntityFrameworkCore.SqlServer.HealthChecks;

/// <summary>Registers bounded SQL Server connectivity and optional migration readiness checks.</summary>
public static class SqlServerHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds SQL Server connectivity readiness and, when requested, a non-mutating pending-migrations check.
    /// </summary>
    /// <typeparam name="TDbContext">Registered SQL Server TCJ DbContext type.</typeparam>
    /// <param name="builder">Health-check builder.</param>
    /// <param name="checkPendingMigrations">Whether to add the optional non-mutating pending-migrations check.</param>
    /// <returns>The same builder for further composition.</returns>
    public static IHealthChecksBuilder AddTcjSqlServer<TDbContext>(
        this IHealthChecksBuilder builder,
        bool checkPendingMigrations = false)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        EnsureInfrastructure(builder.Services);
        IServiceCollection services = builder.Services;
        lock (services)
        {
            if (!services.Any(static descriptor => descriptor.ServiceType == typeof(SqlServerMarker<TDbContext>)))
            {
                services.AddSingleton<SqlServerMarker<TDbContext>>();
                builder.AddCheck<TcjSqlServerHealthCheck<TDbContext>>(
                    TcjHealthCheckNames.Checks.SqlServer,
                    failureStatus: HealthStatus.Unhealthy,
                    tags: [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Ready, TcjHealthCheckNames.Tags.Dependency, TcjHealthCheckNames.Tags.Database, TcjHealthCheckNames.Tags.SqlServer]);
            }

            if (checkPendingMigrations && !services.Any(static descriptor => descriptor.ServiceType == typeof(MigrationMarker<TDbContext>)))
            {
                services.AddSingleton<MigrationMarker<TDbContext>>();
                builder.AddCheck<TcjSqlServerMigrationHealthCheck<TDbContext>>(
                    TcjHealthCheckNames.Checks.SqlServerMigrations,
                    failureStatus: HealthStatus.Unhealthy,
                    tags: [TcjHealthCheckNames.Tags.Tcj, TcjHealthCheckNames.Tags.Ready, TcjHealthCheckNames.Tags.Dependency, TcjHealthCheckNames.Tags.Database, TcjHealthCheckNames.Tags.SqlServer, TcjHealthCheckNames.Tags.Configuration]);
            }
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

    private sealed class SqlServerMarker<TDbContext> where TDbContext : DbContext { public SqlServerMarker() { } }
    private sealed class MigrationMarker<TDbContext> where TDbContext : DbContext { public MigrationMarker() { } }
}
