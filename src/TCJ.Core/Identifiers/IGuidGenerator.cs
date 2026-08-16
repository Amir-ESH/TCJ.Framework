namespace TCJ.Core.Identifiers;

/// <summary>
/// Generates random and time-ordered globally unique identifiers.
/// </summary>
public interface IGuidGenerator
{
    /// <summary>
    /// Creates a random version 4 GUID.
    /// </summary>
    /// <returns>The resulting value.</returns>
    Guid Create();

    /// <summary>
    /// Creates a time-ordered version 7 GUID according to RFC 9562.
    /// </summary>
    /// <returns>The resulting value.</returns>
    Guid CreateVersion7();
}
