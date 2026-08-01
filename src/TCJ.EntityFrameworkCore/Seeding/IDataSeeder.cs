namespace TCJ.EntityFrameworkCore.Seeding;

/// <summary>
/// Coordinates all registered data seed contributors inside one dependency-injection scope.
/// </summary>
public interface IDataSeeder
{
    /// <summary>
    /// Executes registered contributors in deterministic order and commits them atomically.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    Task SeedAsync(CancellationToken cancellationToken = default);
}
