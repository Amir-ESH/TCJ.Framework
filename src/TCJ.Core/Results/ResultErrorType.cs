namespace TCJ.Core.Results;

/// <summary>
/// Describes the semantic category of a result error.
/// </summary>
public enum ResultErrorType
{
    /// <summary>
    /// A general expected failure.
    /// </summary>
    Failure = 0,

    /// <summary>
    /// One or more input values are invalid.
    /// </summary>
    Validation = 1,

    /// <summary>
    /// The requested resource does not exist.
    /// </summary>
    NotFound = 2,

    /// <summary>
    /// The operation conflicts with the current state.
    /// </summary>
    Conflict = 3,

    /// <summary>
    /// The caller is not authenticated.
    /// </summary>
    Unauthorized = 4,

    /// <summary>
    /// The caller is authenticated but not allowed to perform the operation.
    /// </summary>
    Forbidden = 5,

    /// <summary>
    /// An unexpected failure occurred.
    /// </summary>
    Unexpected = 6
}
