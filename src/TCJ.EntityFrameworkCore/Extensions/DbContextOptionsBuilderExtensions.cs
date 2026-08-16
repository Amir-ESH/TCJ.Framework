using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Interceptors;
using TCJ.EntityFrameworkCore.Outbox.Interceptors;

namespace TCJ.EntityFrameworkCore.Extensions;

/// <summary>
/// Provides TCJ-specific DbContext option configuration.
/// </summary>
public static class DbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Adds TCJ persistence interceptors, including transactional-outbox interceptors when outbox services are registered.
    /// </summary>
    /// <param name="optionsBuilder">The options builder value.</param>
    /// <param name="serviceProvider">The service provider.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static DbContextOptionsBuilder AddTcjPersistenceInterceptors(this DbContextOptionsBuilder optionsBuilder,
                                                                        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var interceptors = new List<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>
        {
            serviceProvider.GetRequiredService<AuditingSaveChangesInterceptor>()
        };

        OutboxSaveChangesInterceptor? outboxSaveChanges = serviceProvider.GetService<OutboxSaveChangesInterceptor>();
        OutboxTransactionInterceptor? outboxTransaction = serviceProvider.GetService<OutboxTransactionInterceptor>();
        if (outboxSaveChanges is not null)
        {
            interceptors.Add(outboxSaveChanges);
        }
        if (outboxTransaction is not null)
        {
            interceptors.Add(outboxTransaction);
        }

        return optionsBuilder.AddInterceptors(interceptors);
    }
}
