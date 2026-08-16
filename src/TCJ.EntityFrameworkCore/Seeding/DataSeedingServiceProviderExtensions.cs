using Microsoft.Extensions.DependencyInjection;

namespace TCJ.EntityFrameworkCore.Seeding;

/// <summary>
/// Starts data seeding from an application's root service provider.
/// </summary>
public static class DataSeedingServiceProviderExtensions
{
    /// <summary>
    /// Creates one asynchronous scope and executes all registered seed contributors in it.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    public static async Task SeedTcjDataAsync(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();

        IDataSeeder seeder = scope.ServiceProvider.GetRequiredService<IDataSeeder>();

        await seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
    }
}
