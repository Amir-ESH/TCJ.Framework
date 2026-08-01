using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Interceptors;
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.Searching;
using TCJ.EntityFrameworkCore.Seeding;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TCJ.EntityFrameworkCore.Extensions;

/// <summary>
/// Registers TCJ Entity Framework Core abstractions, repositories, auditing and
/// unit-of-work services.
/// </summary>
public static class EntityFrameworkCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers and configures a DbContext that provides both read and write capabilities,
    /// then registers TCJ repositories, auditing and the unit of work.
    /// </summary>
    public static IServiceCollection AddTcjEntityFrameworkCore<TDbContext>(this IServiceCollection services,
                                                                           Action<DbContextOptionsBuilder> configureDbContext)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDbContext);

        RegisterFrameworkServices(services);

        services.AddDbContext<TDbContext>((serviceProvider, optionsBuilder) =>
        {
            configureDbContext(optionsBuilder);
            optionsBuilder.AddTcjPersistenceInterceptors(serviceProvider);
        });

        return RegisterAbstractions<TDbContext>(services);
    }

    /// <summary>
    /// Registers and configures a DbContext using services from the application container,
    /// then registers TCJ repositories, auditing and the unit of work.
    /// </summary>
    public static IServiceCollection AddTcjEntityFrameworkCore<TDbContext>(this IServiceCollection services,
                                                                           Action<IServiceProvider, DbContextOptionsBuilder> configureDbContext)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDbContext);

        RegisterFrameworkServices(services);

        services.AddDbContext<TDbContext>((serviceProvider, optionsBuilder) =>
        {
            configureDbContext(serviceProvider, optionsBuilder);
            optionsBuilder.AddTcjPersistenceInterceptors(serviceProvider);
        });

        return RegisterAbstractions<TDbContext>(services);
    }

    /// <summary>
    /// Registers TCJ abstractions for a DbContext that has already been registered in
    /// the service collection. The existing DbContext registration must explicitly call
    /// <see cref="DbContextOptionsBuilderExtensions.AddTcjPersistenceInterceptors"/>.
    /// </summary>
    public static IServiceCollection AddTcjEntityFrameworkCore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        RegisterFrameworkServices(services);
        return RegisterAbstractions<TDbContext>(services);
    }

    private static IServiceCollection RegisterAbstractions<TDbContext>(IServiceCollection services)
        where TDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        services.TryAddScoped<IReadDbContext>(implementationFactory: serviceProvider => serviceProvider.GetRequiredService<TDbContext>());

        services.TryAddScoped<IWriteDbContext>(implementationFactory: serviceProvider => serviceProvider.GetRequiredService<TDbContext>());

        RegisterRepositories(services);
        services.TryAddScoped<IUnitOfWork, EfUnitOfWork>();

        return services;
    }

    private static void RegisterFrameworkServices(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<AuditingSaveChangesInterceptor>();
        services.TryAddScoped<IDataSeeder, DataSeeder>();
        services.TryAddScoped<IEntitySearcher, EntitySearcher>();
    }

    private static void RegisterRepositories(IServiceCollection services)
    {
        services.TryAddScoped(service: typeof(IReadRepository<,>), typeof(EfReadRepository<,>));
        services.TryAddScoped(service: typeof(IWriteRepository<,>), typeof(EfWriteRepository<,>));
        services.TryAddScoped(service: typeof(IRepository<,>), typeof(EfRepository<,>));
        services.TryAddScoped(service: typeof(ISoftDeleteRepository<,>), typeof(EfSoftDeleteRepository<,>));

        services.TryAddScoped(service: typeof(IReadRepository<>), typeof(EfReadRepository<>));
        services.TryAddScoped(service: typeof(IWriteRepository<>), typeof(EfWriteRepository<>));
        services.TryAddScoped(service: typeof(IRepository<>), typeof(EfRepository<>));
        services.TryAddScoped(service: typeof(ISoftDeleteRepository<>), typeof(EfSoftDeleteRepository<>));
    }
}
