using System.Linq.Expressions;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Specifications;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TCJ.EntityFrameworkCore.Repositories;

/// <summary>
/// Facade that combines independent read and write repository contracts.
/// Persistence and transaction control remain the responsibility of
/// <see cref="IUnitOfWork"/>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The primary-key type.</typeparam>
public class EfRepository<TEntity, TKey> : IRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    private readonly IReadRepository<TEntity, TKey> _readRepository;
    private readonly IWriteRepository<TEntity, TKey> _writeRepository;

    /// <summary>
    /// Initializes the facade with separate read and write repositories.
    /// </summary>
    public EfRepository(
        IReadRepository<TEntity, TKey> readRepository,
        IWriteRepository<TEntity, TKey> writeRepository)
    {
        ArgumentNullException.ThrowIfNull(readRepository);
        ArgumentNullException.ThrowIfNull(writeRepository);

        _readRepository = readRepository;
        _writeRepository = writeRepository;
    }

    /// <inheritdoc />
    public IQueryable<TEntity> Query() =>
        _readRepository.Query();

    /// <inheritdoc />
    public IQueryable<TEntity> TrackedQuery() =>
        _readRepository.TrackedQuery();

    /// <inheritdoc />
    public IQueryable<TEntity> Query(ISpecification<TEntity> specification) =>
        _readRepository.Query(specification);

    /// <inheritdoc />
    public Task<TEntity?> GetByIdAsync(
        TKey id,
        CancellationToken cancellationToken = default) =>
        _readRepository.GetByIdAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<TEntity?> FirstOrDefaultAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default) =>
        _readRepository.FirstOrDefaultAsync(specification, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TEntity>> ListAsync(
        CancellationToken cancellationToken = default) =>
        _readRepository.ListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TEntity>> ListAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        _readRepository.ListAsync(predicate, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TEntity>> ListAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default) =>
        _readRepository.ListAsync(specification, cancellationToken);

    /// <inheritdoc />
    public Task<bool> AnyAsync(
        CancellationToken cancellationToken = default) =>
        _readRepository.AnyAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        _readRepository.AnyAsync(predicate, cancellationToken);

    /// <inheritdoc />
    public Task<bool> AnyAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default) =>
        _readRepository.AnyAsync(specification, cancellationToken);

    /// <inheritdoc />
    public Task<int> CountAsync(
        CancellationToken cancellationToken = default) =>
        _readRepository.CountAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> CountAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        _readRepository.CountAsync(predicate, cancellationToken);

    /// <inheritdoc />
    public Task<int> CountAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default) =>
        _readRepository.CountAsync(specification, cancellationToken);

    /// <inheritdoc />
    public Task AddAsync(
        TEntity entity,
        CancellationToken cancellationToken = default) =>
        _writeRepository.AddAsync(entity, cancellationToken);

    /// <inheritdoc />
    public Task AddRangeAsync(
        IEnumerable<TEntity> entities,
        CancellationToken cancellationToken = default) =>
        _writeRepository.AddRangeAsync(entities, cancellationToken);

    /// <inheritdoc />
    public void Update(TEntity entity) =>
        _writeRepository.Update(entity);

    /// <inheritdoc />
    public void UpdateRange(IEnumerable<TEntity> entities) =>
        _writeRepository.UpdateRange(entities);

    /// <inheritdoc />
    public void Remove(TEntity entity) =>
        _writeRepository.Remove(entity);

    /// <inheritdoc />
    public void RemoveRange(IEnumerable<TEntity> entities) =>
        _writeRepository.RemoveRange(entities);
}

/// <summary>
/// Repository facade for entities with a <see cref="long"/> key.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public class EfRepository<TEntity> :
    EfRepository<TEntity, long>,
    IRepository<TEntity>
    where TEntity : class, IEntity<long>
{
    /// <summary>
    /// Initializes the facade with separate read and write repositories.
    /// </summary>
    public EfRepository(
        IReadRepository<TEntity, long> readRepository,
        IWriteRepository<TEntity, long> writeRepository)
        : base(readRepository, writeRepository)
    {
    }
}
