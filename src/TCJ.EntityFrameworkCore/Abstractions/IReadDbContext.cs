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
    DbSet<TEntity> Set<TEntity>() where TEntity : class;
}
