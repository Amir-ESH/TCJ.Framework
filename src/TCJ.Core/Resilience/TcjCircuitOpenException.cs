namespace TCJ.Core.Resilience;

/// <summary>
/// Raised when an isolated circuit breaker rejects an operation while open or while another half-open probe is active.
/// </summary>
public sealed class TcjCircuitOpenException : InvalidOperationException
{
    /// <summary>Creates a circuit-open exception.</summary>
    /// <param name="state">The circuit state that rejected the operation.</param>
    public TcjCircuitOpenException(TcjCircuitState state)
        : base($"The TCJ resilience circuit is {state} and rejected the operation.")
    {
        State = state;
    }

    /// <summary>Gets the state that rejected the operation.</summary>
    public TcjCircuitState State { get; }
}
