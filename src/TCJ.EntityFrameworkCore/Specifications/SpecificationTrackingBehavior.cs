namespace TCJ.EntityFrameworkCore.Specifications;

/// <summary>
/// Defines how Entity Framework Core tracks entities returned by a specification.
/// </summary>
public enum SpecificationTrackingBehavior
{
    /// <summary>
    /// Does not track returned entities. This is the default for read specifications.
    /// </summary>
    NoTracking = 0,

    /// <summary>
    /// Tracks returned entities in the current DbContext.
    /// </summary>
    Tracking = 1,

    /// <summary>
    /// Does not track entities but preserves identity resolution within the result set.
    /// </summary>
    NoTrackingWithIdentityResolution = 2
}
