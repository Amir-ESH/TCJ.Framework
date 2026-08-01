using Microsoft.EntityFrameworkCore;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TCJ.EntityFrameworkCore.Repositories;

/// <summary>
/// Entity Framework Core write repository. Operations stage changes and leave
/// persistence and audit stamping to the current unit of work and SaveChanges pipeline.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The primary-key type.</typeparam>
public class EfWriteRepository<TEntity, TKey> : IWriteRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>
    /// Initializes the repository.
    /// </summary>
    public EfWriteRepository(IWriteDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        Db = db;
    }

    /// <summary>
    /// Gets the underlying write context.
    /// </summary>
    protected IWriteDbContext Db { get; }

    /// <summary>
    /// Gets the entity set used by this repository.
    /// </summary>
    protected DbSet<TEntity> DbSet => Db.Set<TEntity>();

    /// <inheritdoc />
    public virtual async Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        await DbSet.AddAsync(entity, cancellationToken)
                   .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TEntity> entityList = MaterializeAndValidate(entities);

        if (entityList.Count == 0)
        {
            return;
        }

        await DbSet.AddRangeAsync(entityList, cancellationToken)
                   .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual void Update(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        DbSet.Update(entity);
    }

    /// <inheritdoc />
    public virtual void UpdateRange(IEnumerable<TEntity> entities)
    {
        IReadOnlyList<TEntity> entityList = MaterializeAndValidate(entities);

        if (entityList.Count != 0)
        {
            DbSet.UpdateRange(entityList);
        }
    }

    /// <inheritdoc />
    public virtual void Remove(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        DbSet.Remove(entity);
    }

    /// <inheritdoc />
    public virtual void RemoveRange(IEnumerable<TEntity> entities)
    {
        IReadOnlyList<TEntity> entityList = MaterializeAndValidate(entities);

        if (entityList.Count != 0)
        {
            DbSet.RemoveRange(entityList);
        }
    }

    private static IReadOnlyList<TEntity> MaterializeAndValidate(
        IEnumerable<TEntity> entities)
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
/// Entity Framework Core write repository for entities with a <see cref="long"/> key.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public class EfWriteRepository<TEntity> : EfWriteRepository<TEntity, long>, IWriteRepository<TEntity>
    where TEntity : class, IEntity<long>
{
    /// <summary>
    /// Initializes the repository.
    /// </summary>
    public EfWriteRepository(IWriteDbContext db) : base(db)
    { }
}
