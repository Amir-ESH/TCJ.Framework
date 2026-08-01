namespace TCJ.EntityFrameworkCore.Seeding;

/// <summary>
/// Contributes one idempotent step to application data initialization.
/// Dependencies required by the contributor should be supplied through constructor injection.
/// </summary>
public interface IDataSeedContributor
{
    /// <summary>
    /// Gets the contributor execution order. Lower values run first.
    /// Contributors with the same order are sorted by their full type name.
    /// </summary>
    int Order => 0;

    /// <summary>
    /// Creates or updates the contributor's seed data.
    /// Implementations should be idempotent because seeding may run more than once.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    Task SeedAsync(CancellationToken cancellationToken = default);
}
