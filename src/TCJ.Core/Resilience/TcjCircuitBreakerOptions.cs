namespace TCJ.Core.Resilience;

/// <summary>
/// Configures an isolated transient-failure circuit breaker.
/// </summary>
public sealed class TcjCircuitBreakerOptions
{
    private static readonly TimeSpan DefaultBreakDuration = TimeSpan.FromSeconds(30);
    /// <summary>Number of consecutive transient failures required to open the circuit.</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>Duration the circuit remains open before one half-open probe is permitted.</summary>
    public TimeSpan BreakDuration { get; set; } = DefaultBreakDuration;

    internal const int MaximumFailureThreshold = 100;
    internal static readonly TimeSpan MaximumBreakDuration = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (FailureThreshold is < 1 or > MaximumFailureThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FailureThreshold),
                $"Circuit-breaker failure threshold must be between 1 and {MaximumFailureThreshold}.");
        }

        if (BreakDuration <= TimeSpan.Zero || BreakDuration > MaximumBreakDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BreakDuration),
                $"Circuit-breaker duration must be greater than zero and no more than {MaximumBreakDuration.TotalMinutes:0} minutes.");
        }
    }

    internal TcjCircuitBreakerOptions Clone() => new()
    {
        FailureThreshold = FailureThreshold,
        BreakDuration = BreakDuration
    };
}
