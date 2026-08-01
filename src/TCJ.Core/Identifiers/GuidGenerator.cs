namespace TCJ.Core.Identifiers;

/// <summary>
/// Generates random version 4 and time-ordered version 7 GUID values.
/// </summary>
public sealed class GuidGenerator : IGuidGenerator
{
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes the generator with the system time provider.
    /// </summary>
    public GuidGenerator()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes the generator with a caller-provided time source.
    /// </summary>
    /// <param name="timeProvider">The time source used by version 7 GUID generation.</param>
    public GuidGenerator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Guid Create() => Guid.NewGuid();

    /// <inheritdoc />
    public Guid CreateVersion7() => Guid.CreateVersion7(_timeProvider.GetUtcNow());
}
