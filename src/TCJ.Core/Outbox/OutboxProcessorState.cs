namespace TCJ.Core.Outbox;

/// <summary>
/// Thread-safe safe-diagnostic state for manual or hosted outbox processing.
/// </summary>
internal sealed class OutboxProcessorState
{
    private readonly object _gate = new();
    private bool _started;
    private DateTimeOffset? _lastSuccessfulPollAtUtc;
    private string? _lastFailureType;

    /// <summary>Gets whether processing has started at least once.</summary>
    internal bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _started;
            }
        }
    }

    /// <summary>Gets the most recent successful processing-poll time.</summary>
    internal DateTimeOffset? LastSuccessfulPollAtUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastSuccessfulPollAtUtc;
            }
        }
    }

    /// <summary>Gets the bounded exception type from the most recent failed poll, if any.</summary>
    internal string? LastFailureType
    {
        get
        {
            lock (_gate)
            {
                return _lastFailureType;
            }
        }
    }

    /// <summary>Marks the processor as started without exposing any message payload.</summary>
    internal void MarkStarted()
    {
        lock (_gate)
        {
            _started = true;
        }
    }

    /// <summary>Records a successful processor poll.</summary>
    internal void MarkSucceeded(DateTimeOffset now)
    {
        lock (_gate)
        {
            _started = true;
            _lastSuccessfulPollAtUtc = now;
            _lastFailureType = null;
        }
    }

    /// <summary>Records a bounded failure category without retaining exception messages or stack traces.</summary>
    internal void MarkFailed(string failureType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureType);
        lock (_gate)
        {
            _started = true;
            _lastFailureType = failureType.Length <= 256 ? failureType : failureType[..256];
        }
    }
}
