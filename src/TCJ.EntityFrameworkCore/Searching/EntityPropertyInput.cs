namespace TCJ.EntityFrameworkCore.Searching;

/// <summary>
/// Identifies a mapped scalar property on an entity.
/// </summary>
public sealed record EntityPropertyInput
{
    /// <summary>
    /// Initializes a new property lookup.
    /// </summary>
    public EntityPropertyInput(string entityName, string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        EntityName = entityName.Trim();
        PropertyName = propertyName.Trim();
    }

    /// <summary>
    /// Gets the mapped CLR full name or unambiguous CLR short name.
    /// </summary>
    public string EntityName { get; }

    /// <summary>
    /// Gets the mapped scalar-property name.
    /// </summary>
    public string PropertyName { get; }
}
