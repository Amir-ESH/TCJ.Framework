namespace TCJ.Core.Resilience;

/// <summary>Represents the bounded states of a TCJ circuit breaker.</summary>
public enum TcjCircuitState
{
    /// <summary>Calls are permitted and transient failures are counted.</summary>
    Closed,

    /// <summary>Calls fail fast until the break duration has elapsed.</summary>
    Open,

    /// <summary>Exactly one recovery probe is permitted.</summary>
    HalfOpen
}
