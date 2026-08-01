using Microsoft.EntityFrameworkCore;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TCJ.EntityFrameworkCore.Repositories;

/// <summary>
/// Entity Framework Core implementation of explicit logical deletion and restoration.
/// Audit values are assigned by the auditing SaveChanges interceptor.
/// </summary>
/// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
/// <typeparam name="TKey">The primary-key type.</typeparam>
public class EfSoftDeleteRepository<TEntity, TKey> : ISoftDeleteRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>, ISoftDelete
{
    private readonly IWriteDbContext _db;

    /// <summary>
    /// Initializes the repository.
    /// </summary>
    public EfSoftDeleteRepository(IWriteDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// Gets the entity set used by this repository.
    /// </summary>
    protected DbSet<TEntity> DbSet => _db.Set<TEntity>();

    /// <inheritdoc />
    public virtual void SoftDelete(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        entity.IsDeleted = true;
        DbSet.Update(entity);
    }

    /// <inheritdoc />
    public virtual void SoftDeleteRange(IEnumerable<TEntity> entities)
    {
        IReadOnlyList<TEntity> entityList = MaterializeAndValidate(entities);

        foreach (TEntity entity in entityList)
        {
            entity.IsDeleted = true;
        }

        if (entityList.Count != 0)
        {
            DbSet.UpdateRange(entityList);
        }
    }

    /// <inheritdoc />
    public virtual void Restore(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        entity.IsDeleted = false;
        entity.DeletedOn = null;
        entity.DeletedBy = null;

        DbSet.Update(entity);
    }

    /// <inheritdoc />
    public virtual void RestoreRange(IEnumerable<TEntity> entities)
    {
        IReadOnlyList<TEntity> entityList = MaterializeAndValidate(entities);

        foreach (TEntity entity in entityList)
        {
            entity.IsDeleted = false;
            entity.DeletedOn = null;
            entity.DeletedBy = null;
        }

        if (entityList.Count != 0)
        {
            DbSet.UpdateRange(entityList);
        }
    }

    private static IReadOnlyList<TEntity> MaterializeAndValidate(IEnumerable<TEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        IReadOnlyList<TEntity> entityList = entities as IReadOnlyList<TEntity> ?? entities.ToArray();

        foreach (var t in entityList)
        {
            if (t is null)
            {
                throw new ArgumentException("The entity collection cannot contain null items.", nameof(entities));
            }
        }

        return entityList;
    }
}

/// <summary>
/// Soft-delete repository for entities with a <see cref="long"/> key.
/// </summary>
/// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
public class EfSoftDeleteRepository<TEntity> : EfSoftDeleteRepository<TEntity, long>, ISoftDeleteRepository<TEntity>
    where TEntity : class, IEntity<long>, ISoftDelete
{
    /// <summary>
    /// Initializes the repository.
    /// </summary>
    public EfSoftDeleteRepository(IWriteDbContext db) : base(db)
    { }
}
