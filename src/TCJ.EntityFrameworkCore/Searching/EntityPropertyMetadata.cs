namespace TCJ.EntityFrameworkCore.Searching;

/// <summary>
/// Describes a scalar property in the current Entity Framework Core model.
/// </summary>
/// <param name="EntityName">The full CLR name of the mapped entity.</param>
/// <param name="PropertyName">The mapped property name.</param>
/// <param name="ClrTypeName">The full CLR type name of the property.</param>
/// <param name="IsNullable">Whether the mapped property accepts null values.</param>
/// <param name="IsPrimaryKey">Whether the property participates in the primary key.</param>
/// <param name="IsShadowProperty">Whether the property exists only in the EF Core model.</param>
/// <param name="IsConcurrencyToken">Whether the property is a concurrency token.</param>
public sealed record EntityPropertyMetadata(
    string EntityName,
    string PropertyName,
    string ClrTypeName,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsShadowProperty,
    bool IsConcurrencyToken);
