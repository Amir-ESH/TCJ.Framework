using Microsoft.EntityFrameworkCore.Infrastructure;

namespace TCJ.EntityFrameworkCore.Abstractions;

/// <summary>
/// Defines the write capabilities required by TCJ repositories and the unit of work.
/// A derived <c>DbContext</c> can implement this interface using its inherited
/// <c>SaveChangesAsync</c> method and <c>Database</c> property.
/// </summary>
public interface IWriteDbContext : IReadDbContext
{
    /// <summary>
    /// Gets the database facade used to create explicit transactions.
    /// </summary>
    DatabaseFacade Database { get; }

    /// <summary>
    /// Persists all tracked changes to the database.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The number of state entries written to the database.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
