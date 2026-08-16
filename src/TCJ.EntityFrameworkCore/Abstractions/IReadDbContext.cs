using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace TCJ.EntityFrameworkCore.Abstractions;

/// <summary>
/// Exposes the read-only capabilities required by TCJ query services.
/// </summary>
public interface IReadDbContext
{
    /// <summary>
    /// Gets the finalized Entity Framework Core model for the current context.
    /// </summary>
    IModel Model { get; }

    /// <summary>
    /// Returns the queryable set for the specified entity type.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The result of the operation.</returns>
    DbSet<TEntity> Set<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors
            | DynamicallyAccessedMemberTypes.NonPublicConstructors
            | DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.PublicFields
            | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.NonPublicFields
            | DynamicallyAccessedMemberTypes.Interfaces)]
        TEntity>()
        where TEntity : class;
}
