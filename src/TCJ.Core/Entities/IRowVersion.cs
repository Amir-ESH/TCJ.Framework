namespace TCJ.Core.Entities;

/// <summary>
/// Represents an entity with a database-managed binary optimistic-concurrency token.
/// Provider-specific modules determine how the token is stored and generated.
/// </summary>
public interface IRowVersion
{
    /// <summary>
    /// Gets the database-managed concurrency token.
    /// </summary>
    byte[] RowVersion { get; }
}
