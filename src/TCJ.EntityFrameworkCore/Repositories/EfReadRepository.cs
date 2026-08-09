using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TCJ.Core.Diagnostics;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Diagnostics;
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
        Observe(
            () => DbSet.AsNoTracking(),
            TcjDiagnosticNames.Activities.RepositoryQuery,
            "query");

    /// <inheritdoc />
    public IQueryable<TEntity> TrackedQuery() =>
        Observe(
            () => DbSet,
            TcjDiagnosticNames.Activities.RepositoryQuery,
            "tracked_query");

    /// <inheritdoc />
    public IQueryable<TEntity> Query(ISpecification<TEntity> specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return Observe(
            () => SpecificationEvaluator.GetQuery(DbSet, specification),
            TcjDiagnosticNames.Activities.RepositoryQuery,
            "query_specification");
    }

    /// <inheritdoc />
    public virtual Task<TEntity?> GetByIdAsync(
        TKey id,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            () => DbSet
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    entity => entity.Id!.Equals(id),
                    cancellationToken),
            TcjDiagnosticNames.Activities.RepositoryGet,
            "get",
            cancellationToken);

    /// <inheritdoc />
    public virtual Task<TEntity?> FirstOrDefaultAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        return ObserveAsync(
            () => SpecificationEvaluator
                .GetQuery(DbSet, specification)
                .FirstOrDefaultAsync(cancellationToken),
            TcjDiagnosticNames.Activities.RepositoryGet,
            "first_or_default",
            cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<TEntity>> ListAsync(
        CancellationToken cancellationToken = default) =>
        ObserveListAsync(
            () => DbSet
                .AsNoTracking()
                .ToListAsync(cancellationToken),
            "list",
            cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<TEntity>> ListAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return ObserveListAsync(
            () => DbSet
                .AsNoTracking()
                .Where(predicate)
                .ToListAsync(cancellationToken),
            "list_predicate",
            cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<TEntity>> ListAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        return ObserveListAsync(
            () => SpecificationEvaluator
                .GetQuery(DbSet, specification)
                .ToListAsync(cancellationToken),
            "list_specification",
            cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<bool> AnyAsync(
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            () => DbSet.AnyAsync(cancellationToken),
            TcjDiagnosticNames.Activities.RepositoryQuery,
            "exists",
            cancellationToken);

    /// <inheritdoc />
    public virtual Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return ObserveAsync(
            () => DbSet.AnyAsync(predicate, cancellationToken),
            TcjDiagnosticNames.Activities.RepositoryQuery,
            "exists_predicate",
            cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<bool> AnyAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        return ObserveAsync(
            () => SpecificationEvaluator
                .GetCountQuery(DbSet, specification)
                .AnyAsync(cancellationToken),
            TcjDiagnosticNames.Activities.RepositoryQuery,
            "exists_specification",
            cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<int> CountAsync(
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            () => DbSet.CountAsync(cancellationToken),
            TcjDiagnosticNames.Activities.RepositoryQuery,
            "count",
            cancellationToken);

    /// <inheritdoc />
    public virtual Task<int> CountAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return ObserveAsync(
            () => DbSet.CountAsync(predicate, cancellationToken),
            TcjDiagnosticNames.Activities.RepositoryQuery,
            "count_predicate",
            cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<int> CountAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        return ObserveAsync(
            () => SpecificationEvaluator
                .GetCountQuery(DbSet, specification)
                .CountAsync(cancellationToken),
            TcjDiagnosticNames.Activities.RepositoryQuery,
            "count_specification",
            cancellationToken);
    }

    private TResult Observe<TResult>(
        Func<TResult> operation,
        string activityName,
        string operationName)
    {
        RepositoryTelemetryState telemetry = StartTelemetry(activityName, operationName);
        try
        {
            TResult result = operation();
            telemetry.CompleteSuccess();
            return result;
        }
        catch (Exception exception)
        {
            telemetry.CompleteFailure(exception);
            throw;
        }
    }

    private Task<T> ObserveAsync<T>(
        Func<Task<T>> operation,
        string activityName,
        string operationName,
        CancellationToken cancellationToken)
    {
        RepositoryTelemetryState telemetry = StartTelemetry(activityName, operationName);
        try
        {
            Task<T> task = operation();
            return telemetry.IsActive
                ? ObserveWithTelemetryAsync(task, telemetry, cancellationToken)
                : task;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.CompleteCanceled(exception);
            throw;
        }
        catch (Exception exception)
        {
            telemetry.CompleteFailure(exception);
            throw;
        }
    }

    private Task<IReadOnlyList<TEntity>> ObserveListAsync(
        Func<Task<List<TEntity>>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        RepositoryTelemetryState telemetry = StartTelemetry(
            TcjDiagnosticNames.Activities.RepositoryQuery,
            operationName);

        try
        {
            Task<List<TEntity>> task = operation();
            return telemetry.IsActive
                ? ObserveListWithTelemetryAsync(task, telemetry, cancellationToken)
                : task;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.CompleteCanceled(exception);
            throw;
        }
        catch (Exception exception)
        {
            telemetry.CompleteFailure(exception);
            throw;
        }
    }

    private RepositoryTelemetryState StartTelemetry(string activityName, string operationName) =>
        EntityFrameworkCoreTelemetryDiagnostics.StartRepositoryOperation(
            activityName,
            operationName,
            GetType(),
            typeof(TEntity),
            Db);

    private static async Task<T> ObserveWithTelemetryAsync<T>(
        Task<T> operation,
        RepositoryTelemetryState telemetry,
        CancellationToken cancellationToken)
    {
        try
        {
            T result = await operation.ConfigureAwait(false);
            telemetry.CompleteSuccess();
            return result;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.CompleteCanceled(exception);
            throw;
        }
        catch (Exception exception)
        {
            telemetry.CompleteFailure(exception);
            throw;
        }
    }

    private static async Task<IReadOnlyList<TEntity>> ObserveListWithTelemetryAsync(
        Task<List<TEntity>> operation,
        RepositoryTelemetryState telemetry,
        CancellationToken cancellationToken)
    {
        try
        {
            List<TEntity> result = await operation.ConfigureAwait(false);
            telemetry.CompleteSuccess();
            return result;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.CompleteCanceled(exception);
            throw;
        }
        catch (Exception exception)
        {
            telemetry.CompleteFailure(exception);
            throw;
        }
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
