using System.Diagnostics.CodeAnalysis;

namespace TCJ.EntityFrameworkCore.Searching;

/// <summary>
/// Provides safe runtime lookup operations for entities registered in the current
/// Entity Framework Core model.
/// </summary>
public interface IEntitySearcher
{
    /// <summary>
    /// Determines whether an entity with the supplied primary-key values exists.
    /// Global query filters remain enabled.
    /// </summary>
    /// <param name="input">The search input.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>true when the condition is satisfied; otherwise, false.</returns>
    [RequiresUnreferencedCode("Entity search creates entity-specific executors from runtime EF model metadata. Native AOT consumers should use statically typed repository or DbContext queries that EF tooling can precompile.")]
    [RequiresDynamicCode("Entity search closes generic predicate and executor types from runtime EF model metadata. Native AOT consumers should use statically typed repository or DbContext queries that EF tooling can precompile.")]
    Task<bool> ExistsAsync(EntityRecordInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an entity with the supplied primary-key values, or <see langword="null"/>
    /// when no matching entity is visible through the current query filters.
    /// The returned entity is not tracked.
    /// </summary>
    /// <param name="input">The search input.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The resulting value.</returns>
    [RequiresUnreferencedCode("Entity search creates entity-specific executors from runtime EF model metadata. Native AOT consumers should use statically typed repository or DbContext queries that EF tooling can precompile.")]
    [RequiresDynamicCode("Entity search closes generic predicate and executor types from runtime EF model metadata. Native AOT consumers should use statically typed repository or DbContext queries that EF tooling can precompile.")]
    Task<object?> FindAsync(EntityRecordInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns metadata for a mapped scalar property.
    /// </summary>
    /// <param name="input">The search input.</param>
    /// <returns>The resulting value.</returns>
    EntityPropertyMetadata GetPropertyMetadata(EntityPropertyInput input);
}
