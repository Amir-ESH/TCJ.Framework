using TCJ.Core.Entities;

namespace TCJ.EntityFrameworkCore.Repositories;

/// <summary>
/// Defines explicit logical-deletion and restoration operations.
/// Persistence remains the responsibility of the current unit of work.
/// </summary>
/// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
/// <typeparam name="TKey">The primary-key type.</typeparam>
public interface ISoftDeleteRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>, ISoftDelete
{
    /// <summary>
    /// Marks an entity as logically deleted.
    /// </summary>
    /// <param name="entity">The entity to process.</param>
    void SoftDelete(TEntity entity);

    /// <summary>
    /// Marks multiple entities as logically deleted.
    /// </summary>
    /// <param name="entities">The entities to process.</param>
    void SoftDeleteRange(IEnumerable<TEntity> entities);

    /// <summary>
    /// Restores a logically deleted entity.
    /// </summary>
    /// <param name="entity">The entity to process.</param>
    void Restore(TEntity entity);

    /// <summary>
    /// Restores multiple logically deleted entities.
    /// </summary>
    /// <param name="entities">The entities to process.</param>
    void RestoreRange(IEnumerable<TEntity> entities);
}

/// <summary>
/// Soft-delete repository for entities using <see cref="long"/> primary keys.
/// </summary>
/// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
public interface ISoftDeleteRepository<TEntity> : ISoftDeleteRepository<TEntity, long>
    where TEntity : class, IEntity<long>, ISoftDelete;
