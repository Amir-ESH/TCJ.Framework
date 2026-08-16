using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Diagnostics;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Diagnostics;
using TCJ.EntityFrameworkCore.SqlServer.Options;

namespace TCJ.EntityFrameworkCore.SqlServer.Extensions;

/// <summary>
/// Registers TCJ Entity Framework Core services with the SQL Server provider.
/// </summary>
public static class SqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers a TCJ DbContext configured for SQL Server by using a fixed connection string.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core database context type.</typeparam>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="connectionString">The SQL Server connection string.</param>
    /// <param name="configureTcjSqlServer">An optional delegate used to configure TCJ SQL Server options.</param>
    /// <param name="configureProvider">An optional delegate used to configure the EF Core SQL Server provider.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjSqlServer<TDbContext>(this IServiceCollection services,
                                                                 string connectionString,
                                                                 Action<TcjSqlServerOptions>? configureTcjSqlServer = null,
                                                                 Action<SqlServerDbContextOptionsBuilder>? configureProvider = null)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddTcjSqlServer<TDbContext>(_ => connectionString, configureTcjSqlServer, configureProvider);
    }

    /// <summary>
    /// Registers a TCJ DbContext configured for SQL Server by resolving its connection string
    /// from the application service provider.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core database context type.</typeparam>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="connectionStringFactory">The factory that resolves the SQL Server connection string.</param>
    /// <param name="configureTcjSqlServer">An optional delegate used to configure TCJ SQL Server options.</param>
    /// <param name="configureProvider">An optional delegate used to configure the EF Core SQL Server provider.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjSqlServer<TDbContext>(this IServiceCollection services,
                                                                 Func<IServiceProvider, string> connectionStringFactory,
                                                                 Action<TcjSqlServerOptions>? configureTcjSqlServer = null,
                                                                 Action<SqlServerDbContextOptionsBuilder>? configureProvider = null)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionStringFactory);

        var tcjSqlServerOptions = new TcjSqlServerOptions();
        configureTcjSqlServer?.Invoke(tcjSqlServerOptions);
        tcjSqlServerOptions.Validate();

        return services.AddTcjEntityFrameworkCore<TDbContext>((serviceProvider, optionsBuilder) =>
        {
            using var activity = SqlServerTelemetryDiagnostics.StartConfigureActivity();
            try
            {
                string connectionString = connectionStringFactory(serviceProvider);
                ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

                optionsBuilder.UseSqlServer(connectionString, sqlServerOptionsBuilder =>
                {
                    tcjSqlServerOptions.Apply(sqlServerOptionsBuilder);
                    configureProvider?.Invoke(sqlServerOptionsBuilder);
                });

                TcjTelemetry.CompleteSuccess(activity);
            }
            catch (Exception exception)
            {
                TcjTelemetry.CompleteFailure(activity, exception);
                throw;
            }
        });
    }
}
