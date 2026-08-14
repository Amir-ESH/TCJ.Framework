using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Outbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Outbox;
using TCJ.EntityFrameworkCore.Outbox.Extensions;

namespace TCJ.EntityFrameworkCore.SqlServer.Outbox.Extensions;

/// <summary>Registers SQL Server transactional-outbox claiming and persistence operations.</summary>
public static class SqlServerOutboxServiceCollectionExtensions
{
    /// <summary>
    /// Enables the TCJ transactional outbox for a SQL Server-backed DbContext.
    /// Claims use short, parameterized UPDLOCK/READPAST/READCOMMITTEDLOCK statements and bounded leases.
    /// </summary>
    /// <typeparam name="TDbContext">TCJ SQL Server DbContext that owns the outbox table.</typeparam>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configure">Optional bounded outbox configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjSqlServerOutbox<TDbContext>(
        this IServiceCollection services,
        Action<TcjOutboxOptions>? configure = null)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTcjOutbox<TDbContext>(configure);

        lock (services)
        {
            ServiceDescriptor? existing = services.LastOrDefault(static descriptor => descriptor.ServiceType == typeof(IOutboxStorage));
            if (existing is not null)
            {
                if (existing.ImplementationType == typeof(SqlServerOutboxStorage<TDbContext>))
                {
                    return services;
                }

                throw new InvalidOperationException("A conflicting outbox storage implementation is already registered. Register exactly one provider-specific processor.");
            }

            services.AddScoped<IOutboxStorage, SqlServerOutboxStorage<TDbContext>>();
        }

        return services;
    }
}
