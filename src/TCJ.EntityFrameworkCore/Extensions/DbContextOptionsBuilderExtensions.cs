using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Interceptors;

namespace TCJ.EntityFrameworkCore.Extensions;

/// <summary>
/// Provides TCJ-specific DbContext option configuration.
/// </summary>
public static class DbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Adds the TCJ auditing interceptor to a DbContext that is registered manually.
    /// </summary>
    public static DbContextOptionsBuilder AddTcjPersistenceInterceptors(this DbContextOptionsBuilder optionsBuilder,
                                                                        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return optionsBuilder.AddInterceptors(
            serviceProvider.GetRequiredService<AuditingSaveChangesInterceptor>());
    }
}
