using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Specifications;

namespace TCJ.EntityFrameworkCore.Repositories;

/// <summary>
/// Entity Framework Core read repository. Ordinary reads are no-tracking by default.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The primary-key type.</typeparam>
public class EfReadRepository<TEntity, TKey> : IReadRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>
    /// Initializes a repository for the supplied read context.
    /// </summary>
    public EfReadRepository(IReadDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        Db = db;
    }

    /// <summary>
    /// Gets the underlying read context.
    /// </summary>
    protected IReadDbContext Db { get; }

    /// <summary>
    /// Gets the entity set used by this repository.
    /// </summary>
    protected DbSet<TEntity> DbSet => Db.Set<TEntity>();

    /// <inheritdoc />
    public IQueryable<TEntity> Query() =>
        DbSet.AsNoTracking();

    /// <inheritdoc />
    public IQueryable<TEntity> TrackedQuery() =>
        DbSet;

    /// <inheritdoc />
    public IQueryable<TEntity> Query(ISpecification<TEntity> specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return SpecificationEvaluator.GetQuery(DbSet, specification);
    }

    /// <inheritdoc />
    public virtual Task<TEntity?> GetByIdAsync(
        TKey id,
        CancellationToken cancellationToken = default) =>
        DbSet
            .AsNoTracking()
            .FirstOrDefaultAsync(
                entity => entity.Id!.Equals(id),
                cancellationToken);

    /// <inheritdoc />
    public virtual Task<TEntity?> FirstOrDefaultAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        return SpecificationEvaluator
            .GetQuery(DbSet, specification)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<TEntity>> ListAsync(
        CancellationToken cancellationToken = default) =>
        await DbSet
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<TEntity>> ListAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return await DbSet
            .AsNoTracking()
            .Where(predicate)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyList<TEntity>> ListAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        return await SpecificationEvaluator
            .GetQuery(DbSet, specification)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual Task<bool> AnyAsync(
        CancellationToken cancellationToken = default) =>
        DbSet.AnyAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return DbSet.AnyAsync(predicate, cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<bool> AnyAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        return SpecificationEvaluator
            .GetCountQuery(DbSet, specification)
            .AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<int> CountAsync(
        CancellationToken cancellationToken = default) =>
        DbSet.CountAsync(cancellationToken);

    /// <inheritdoc />
    public virtual Task<int> CountAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return DbSet.CountAsync(predicate, cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<int> CountAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        return SpecificationEvaluator
            .GetCountQuery(DbSet, specification)
            .CountAsync(cancellationToken);
    }
}

/// <summary>
/// Entity Framework Core read repository for entities with a <see cref="long"/> key.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public class EfReadRepository<TEntity> :
    EfReadRepository<TEntity, long>,
    IReadRepository<TEntity>
    where TEntity : class, IEntity<long>
{
    /// <summary>
    /// Initializes a repository for the supplied read context.
    /// </summary>
    public EfReadRepository(IReadDbContext db)
        : base(db)
    {
    }
}
