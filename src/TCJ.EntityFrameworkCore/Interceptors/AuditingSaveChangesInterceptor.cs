using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Entities;
using TCJ.Core.Security;

namespace TCJ.EntityFrameworkCore.Interceptors;

/// <summary>
/// Applies creation, modification and logical-deletion audit values immediately
/// before Entity Framework Core persists tracked changes.
/// </summary>
public sealed class AuditingSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes the interceptor.
    /// </summary>
    public AuditingSaveChangesInterceptor(IServiceProvider serviceProvider, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Applies audit values immediately before Entity Framework Core saves tracked changes.
    /// </summary>
    /// <param name="eventData">Contextual information for the current save operation.</param>
    /// <param name="result">The current interception result.</param>
    /// <returns>The interception result returned by the base interceptor.</returns>
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ApplyAuditValues(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <summary>
    /// Applies audit values immediately before Entity Framework Core asynchronously saves tracked changes.
    /// </summary>
    /// <param name="eventData">Contextual information for the current save operation.</param>
    /// <param name="result">The current interception result.</param>
    /// <param name="cancellationToken">A token used to cancel the asynchronous operation.</param>
    /// <returns>A value task containing the interception result returned by the base interceptor.</returns>
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
                                                                          InterceptionResult<int> result,
                                                                          CancellationToken cancellationToken = default)
    {
        ApplyAuditValues(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ApplyAuditValues(DbContext? dbContext)
    {
        if (dbContext is null)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        long? userId = _serviceProvider.GetService<ICurrentUserProvider>()?.UserId;

        foreach (EntityEntry entry in dbContext.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (entry.Entity is IAuditedEntity auditedEntity)
            {
                ApplyAuditValues(entry, auditedEntity, now, userId);
            }

            if (entry.Entity is ISoftDelete softDeleteEntity)
            {
                ApplySoftDeleteValues(softDeleteEntity, now, userId);
            }
        }
    }

    private static void ApplyAuditValues(EntityEntry entry, IAuditedEntity entity, DateTimeOffset now, long? userId)
    {
        if (entry.State == EntityState.Added)
        {
            entity.CreatedOn ??= now;
            entity.CreatedBy ??= userId;
            entity.ModifiedOn = null;
            entity.ModifiedBy = null;
            return;
        }

        PreventModification(entry, nameof(IAuditedEntity.CreatedOn));
        PreventModification(entry, nameof(IAuditedEntity.CreatedBy));

        entity.ModifiedOn = now;
        entity.ModifiedBy = userId;
    }

    private static void ApplySoftDeleteValues(ISoftDelete entity, DateTimeOffset now, long? userId)
    {
        if (entity.IsDeleted)
        {
            entity.DeletedOn ??= now;
            entity.DeletedBy ??= userId;
            return;
        }

        entity.DeletedOn = null;
        entity.DeletedBy = null;
    }

    private static void PreventModification(EntityEntry entry, string propertyName)
    {
        if (entry.Metadata.FindProperty(propertyName) is not null)
        {
            entry.Property(propertyName).IsModified = false;
        }
    }
}
