using TCJ.Core.Entities;

namespace TCJ.EntityFrameworkCore.Repositories;

/// <summary>
/// Defines operations that stage entity state changes in the current unit of work.
/// These operations do not persist changes. Physical removal is explicit; logical
/// deletion is provided separately by <see cref="ISoftDeleteRepository{TEntity,TKey}"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The primary-key type.</typeparam>
public interface IWriteRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>
    /// Stages an entity for insertion.
    /// </summary>
    Task AddAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages multiple entities for insertion.
    /// </summary>
    Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an entity as modified.
    /// </summary>
    void Update(TEntity entity);

    /// <summary>
    /// Marks multiple entities as modified.
    /// </summary>
    void UpdateRange(IEnumerable<TEntity> entities);

    /// <summary>
    /// Marks an entity for physical removal.
    /// </summary>
    void Remove(TEntity entity);

    /// <summary>
    /// Marks multiple entities for physical removal.
    /// </summary>
    void RemoveRange(IEnumerable<TEntity> entities);
}

/// <summary>
/// Write repository for entities using <see cref="long"/> primary keys.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IWriteRepository<TEntity> : IWriteRepository<TEntity, long>
    where TEntity : class, IEntity<long>;
