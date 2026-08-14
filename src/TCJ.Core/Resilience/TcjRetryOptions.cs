namespace TCJ.Core.Resilience;

/// <summary>
/// Configures bounded operation-level retries.
/// </summary>
public sealed class TcjRetryOptions
{
    private static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(5);
    /// <summary>Maximum number of retries after the initial attempt. Zero disables retry.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base delay used by the exponential backoff schedule.</summary>
    public TimeSpan BaseDelay { get; set; } = DefaultBaseDelay;

    /// <summary>Maximum delay permitted between retry attempts.</summary>
    public TimeSpan MaxDelay { get; set; } = DefaultMaxDelay;

    /// <summary>Gets or sets whether bounded jitter is applied to retry delays.</summary>
    public bool UseJitter { get; set; } = true;

    internal const int MaximumAllowedRetryAttempts = 5;
    internal static readonly TimeSpan MaximumAllowedDelay = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (MaxRetryAttempts is < 0 or > MaximumAllowedRetryAttempts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetryAttempts),
                $"Retry attempts must be between 0 and {MaximumAllowedRetryAttempts}.");
        }

        if (BaseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseDelay), "Retry base delay cannot be negative.");
        }

        if (MaxDelay < TimeSpan.Zero || MaxDelay > MaximumAllowedDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxDelay),
                $"Retry maximum delay must be between zero and {MaximumAllowedDelay.TotalSeconds:0} seconds.");
        }

        if (MaxRetryAttempts > 0 && BaseDelay > MaxDelay)
        {
            throw new ArgumentException("Retry base delay cannot exceed the maximum delay.", nameof(BaseDelay));
        }
    }

    internal TcjRetryOptions Clone() => new()
    {
        MaxRetryAttempts = MaxRetryAttempts,
        BaseDelay = BaseDelay,
        MaxDelay = MaxDelay,
        UseJitter = UseJitter
    };
}
