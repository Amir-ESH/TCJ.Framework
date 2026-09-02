using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Inbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Inbox.Extensions;
using TCJ.EntityFrameworkCore.Inbox.Storage;

namespace TCJ.EntityFrameworkCore.SqlServer.Inbox.Extensions;

/// <summary>Registers SQL Server transactional Inbox persistence and concurrent claiming.</summary>
public static class SqlServerInboxServiceCollectionExtensions
{
    /// <summary>Enables a SQL Server-backed transactional Inbox for one consumer DbContext.</summary>
    /// <typeparam name="TDbContext">SQL Server DbContext that owns Inbox and business state.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Required Inbox options including stable consumer name.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjSqlServerInbox<TDbContext>(this IServiceCollection services, Action<TcjInboxOptions> configure)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTcjInbox<TDbContext>(configure);
        lock (services)
        {
            ServiceDescriptor? existing = services.LastOrDefault(static d => d.ServiceType == typeof(IInboxStorage));
            if (existing is not null)
            {
                if (existing.ImplementationType == typeof(SqlServerInboxStorage<TDbContext>)) return services;
                throw new InvalidOperationException("A conflicting Inbox storage implementation is already registered. Register exactly one provider-specific storage.");
            }
            services.AddScoped<IInboxStorage, SqlServerInboxStorage<TDbContext>>();
        }
        return services;
    }
}
