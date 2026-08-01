using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TCJ.EntityFrameworkCore.Seeding;

/// <summary>
/// Executes all registered seed contributors sequentially in a single database transaction.
/// </summary>
public sealed class DataSeeder : IDataSeeder
{
    private readonly IReadOnlyList<IDataSeedContributor> _contributors;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>
    /// Initializes the data seeder.
    /// </summary>
    public DataSeeder(IEnumerable<IDataSeedContributor> contributors, IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        ArgumentNullException.ThrowIfNull(unitOfWork);

        _contributors = contributors.OrderBy(contributor => contributor.Order)
                                    .ThenBy(contributor => contributor.GetType().FullName, StringComparer.Ordinal)
                                    .ToArray();

        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (_contributors.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await using IUnitOfWorkTransaction transaction =
            await _unitOfWork.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (IDataSeedContributor contributor in _contributors)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await contributor.SeedAsync(cancellationToken).ConfigureAwait(false);

                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception seedingException)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException("Data seeding failed and the database transaction could not be rolled back.", 
                                             seedingException, rollbackException);
            }

            throw;
        }
    }
}
