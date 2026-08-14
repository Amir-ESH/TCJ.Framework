using System.Collections.Concurrent;

namespace TCJ.AspNetCore.IntegrationTests.TestHost;

internal sealed class ScopedMarker(ScopedDisposalTracker tracker) : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();

    public void Dispose() => tracker.Record(Id);
}

internal sealed class TransientMarker
{
    public Guid Id { get; } = Guid.NewGuid();
}

internal sealed class SingletonMarker
{
    public Guid Id { get; } = Guid.NewGuid();
}

internal sealed class ScopedDisposalTracker
{
    private readonly ConcurrentDictionary<Guid, byte> _disposed = new();

    public void Record(Guid id) => _disposed.TryAdd(id, 0);

    public bool WasDisposed(Guid id) => _disposed.ContainsKey(id);
}

internal sealed class CancellationObserver
{
    private TaskCompletionSource<bool> _cancellation = CreateSource();

    public Task<bool> WaitAsync(CancellationToken cancellationToken = default)
        => _cancellation.Task.WaitAsync(cancellationToken);

    public void Signal(bool requestAborted)
        => _cancellation.TrySetResult(requestAborted);

    public void Reset() => Interlocked.Exchange(ref _cancellation, CreateSource());

    private static TaskCompletionSource<bool> CreateSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record EchoPayload(string Value);
