using TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;

namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;

internal interface ISagaProviderStorage
{
    string ProviderName { get; }
    Task ReserveCorrelationAsync(SagaCorrelation correlation, CancellationToken cancellationToken);
    Task<IReadOnlyList<SagaTimerClaim>> ClaimDueTimersAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task RecordTimerFailureAsync(Guid timerId, Guid lockId, int attempt, string failureType, bool retry, DateTimeOffset? nextAttemptAtUtc, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed record SagaTimerClaim(Guid TimerId, Guid SagaId, string SagaType, string TimerName, string TimeoutType, DateTimeOffset DueAtUtc, int Attempt, Guid LockId);
