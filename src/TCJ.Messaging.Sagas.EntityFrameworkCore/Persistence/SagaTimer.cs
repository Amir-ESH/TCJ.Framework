namespace TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;

internal sealed class SagaTimer
{
    private SagaTimer() { }

    internal SagaTimer(Guid timerId, Guid sagaId, string sagaType, string timerName, string timeoutType, DateTimeOffset dueAtUtc, DateTimeOffset now)
    {
        TimerId = timerId;
        SagaId = sagaId;
        SagaType = sagaType;
        TimerName = timerName;
        TimeoutType = timeoutType;
        DueAtUtc = dueAtUtc;
        Status = SagaTimerStatus.Scheduled;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid TimerId { get; private set; }
    public Guid SagaId { get; private set; }
    public string SagaType { get; private set; } = string.Empty;
    public string TimerName { get; private set; } = string.Empty;
    public string TimeoutType { get; private set; } = string.Empty;
    public DateTimeOffset DueAtUtc { get; internal set; }
    public SagaTimerStatus Status { get; internal set; }
    public int AttemptCount { get; internal set; }
    public Guid? LockId { get; internal set; }
    public DateTimeOffset? LockedAtUtc { get; internal set; }
    public DateTimeOffset? LockExpiresAtUtc { get; internal set; }
    public DateTimeOffset? CompletedAtUtc { get; internal set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; internal set; }
    public string? LastFailureType { get; internal set; }
    public byte[] ConcurrencyToken { get; private set; } = [];
}
