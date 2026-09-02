namespace TCJ.EntityFrameworkCore.Inbox.Processing;

internal sealed class InboxProcessorState
{
    private readonly object _gate = new();
    private bool _started;
    private string? _lastFailureType;
    internal bool IsStarted { get { lock (_gate) return _started; } }
    internal string? LastFailureType { get { lock (_gate) return _lastFailureType; } }
    internal void MarkStarted() { lock (_gate) _started = true; }
    internal void MarkSucceeded() { lock (_gate) { _started = true; _lastFailureType = null; } }
    internal void MarkFailed(string failureType) { lock (_gate) { _started = true; _lastFailureType = failureType.Length <= 64 ? failureType : failureType[..64]; } }
}
