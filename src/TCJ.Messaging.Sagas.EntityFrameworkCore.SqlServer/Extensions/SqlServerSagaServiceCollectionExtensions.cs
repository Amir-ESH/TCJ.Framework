using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.Messaging.Sagas.Configuration;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;
using TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer.Processing;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer.Extensions;

/// <summary>Registers SQL Server-specific durable Saga persistence behavior.</summary>
public static class SqlServerSagaServiceCollectionExtensions
{
    /// <summary>Enables SQL Server rowversion and atomic lease-based Saga timer/correlation storage.</summary>
    /// <typeparam name="TDbContext">Application DbContext shared with transactional Inbox and Outbox.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Optional bounded Saga configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddTcjSqlServerSagas<TDbContext>(this IServiceCollection services, Action<TcjSagaOptions>? configure = null)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTcjSagas<TDbContext>(configure);
        lock (services)
        {
            ServiceDescriptor? existing = services.LastOrDefault(static descriptor => descriptor.ServiceType == typeof(ISagaProviderStorage));
            if (existing is not null)
            {
                if (existing.ImplementationType == typeof(SqlServerSagaProviderStorage<TDbContext>)) return services;
                throw new InvalidOperationException("A conflicting Saga provider storage is already registered. Register exactly one provider-specific Saga storage.");
            }
            services.AddScoped<ISagaProviderStorage, SqlServerSagaProviderStorage<TDbContext>>();
        }
        return services;
    }
}
