namespace TCJ.Core.Resilience;

/// <summary>
/// Raised when an explicit TCJ timeout policy cancels an operation before the caller cancels it.
/// </summary>
public sealed class TcjTimeoutException : TimeoutException
{
    /// <summary>Creates a timeout exception for the configured timeout.</summary>
    /// <param name="timeout">The configured operation timeout.</param>
    /// <param name="innerException">The cancellation observed from the timed-out operation.</param>
    public TcjTimeoutException(TimeSpan timeout, Exception innerException)
        : base($"The TCJ resilience operation exceeded its configured timeout of {timeout}.", innerException)
    {
        Timeout = timeout;
    }

    /// <summary>Gets the configured timeout that elapsed.</summary>
    public TimeSpan Timeout { get; }
}
