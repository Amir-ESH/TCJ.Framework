namespace TCJ.Core.Resilience;

/// <summary>
/// Configures a cooperative operation timeout.
/// </summary>
public sealed class TcjTimeoutOptions
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Timeout applied to one logical operation.</summary>
    public TimeSpan OperationTimeout { get; set; } = DefaultOperationTimeout;

    internal static readonly TimeSpan MaximumAllowedTimeout = TimeSpan.FromSeconds(120);

    internal void Validate()
    {
        if (OperationTimeout <= TimeSpan.Zero || OperationTimeout > MaximumAllowedTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OperationTimeout),
                $"Operation timeout must be greater than zero and no more than {MaximumAllowedTimeout.TotalSeconds:0} seconds.");
        }
    }

    internal TcjTimeoutOptions Clone() => new() { OperationTimeout = OperationTimeout };
}
