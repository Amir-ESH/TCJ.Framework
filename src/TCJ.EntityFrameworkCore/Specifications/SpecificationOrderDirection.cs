namespace TCJ.EntityFrameworkCore.Specifications;

/// <summary>
/// Defines the direction of an ordering expression in a specification.
/// </summary>
public enum SpecificationOrderDirection
{
    /// <summary>
    /// Sorts values from lowest to highest.
    /// </summary>
    Ascending = 0,

    /// <summary>
    /// Sorts values from highest to lowest.
    /// </summary>
    Descending = 1
}
