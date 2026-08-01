using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TCJ.EntityFrameworkCore.Seeding;

/// <summary>
/// Registers application-specific data seed contributors.
/// </summary>
public static class DataSeedingServiceCollectionExtensions
{
    /// <summary>
    /// Registers a scoped seed contributor without adding duplicate registrations for
    /// the same implementation type.
    /// </summary>
    public static IServiceCollection AddTcjDataSeedContributor<TContributor>(this IServiceCollection services)
        where TContributor : class, IDataSeedContributor
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDataSeedContributor, TContributor>());

        return services;
    }
}
