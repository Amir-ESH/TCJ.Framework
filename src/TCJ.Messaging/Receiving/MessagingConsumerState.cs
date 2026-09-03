namespace TCJ.Messaging.Receiving;

/// <summary>Bounded in-process consumer state used by readiness diagnostics.</summary>
public sealed class MessagingConsumerState
{
    private int _running; private long _active; private string? _lastFailureType;
    /// <summary>Gets whether a runner is active.</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    /// <summary>Gets current active messages.</summary>
    public long ActiveMessages => Interlocked.Read(ref _active);
    /// <summary>Gets last bounded failure type.</summary>
    public string? LastFailureType => Volatile.Read(ref _lastFailureType);
    internal void Start() { if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("The messaging consumer runner is already running."); Volatile.Write(ref _lastFailureType, null); }
    internal void Stop() => Volatile.Write(ref _running, 0);
    internal void MessageStarted() => Interlocked.Increment(ref _active);
    internal void MessageStopped() => Interlocked.Decrement(ref _active);
    internal void Fail(string failureType) => Volatile.Write(ref _lastFailureType, failureType);
}
