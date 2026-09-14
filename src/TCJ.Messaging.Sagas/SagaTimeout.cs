namespace TCJ.Messaging.Sagas;

/// <summary>Stable metadata for a registered Saga-owned timeout.</summary>
/// <param name="TimerName">Stable timer name defined by the Saga.</param>
/// <param name="TimeoutType">Stable logical timeout contract name.</param>
/// <param name="DueAtUtc">Original UTC deadline.</param>
/// <param name="Attempt">One-based timeout execution attempt.</param>
public sealed record SagaTimeout(string TimerName, string TimeoutType, DateTimeOffset DueAtUtc, int Attempt);
