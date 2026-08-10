namespace TCJ.Core.Resilience;

/// <summary>
/// Groups the backend-neutral TCJ resilience defaults used by explicit policies.
/// Registering these options does not automatically wrap application operations.
/// </summary>
public sealed class TcjResilienceOptions
{
    /// <summary>Initializes the default bounded resilience option groups.</summary>
    public TcjResilienceOptions()
    {
        Retry = new TcjRetryOptions();
        Timeout = new TcjTimeoutOptions();
        CircuitBreaker = new TcjCircuitBreakerOptions();
    }

    /// <summary>Gets retry configuration.</summary>
    public TcjRetryOptions Retry { get; }

    /// <summary>Gets timeout configuration.</summary>
    public TcjTimeoutOptions Timeout { get; }

    /// <summary>Gets circuit-breaker configuration.</summary>
    public TcjCircuitBreakerOptions CircuitBreaker { get; }

    internal void Validate()
    {
        Retry.Validate();
        Timeout.Validate();
        CircuitBreaker.Validate();
    }
}
