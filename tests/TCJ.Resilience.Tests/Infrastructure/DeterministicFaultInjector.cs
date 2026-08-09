using System.Collections.Concurrent;

namespace TCJ.Resilience.Tests.Infrastructure;

internal sealed class DeterministicFaultInjector
{
    private readonly HashSet<int> _failureAttempts;
    private readonly HashSet<int> _delayAttempts;
    private readonly HashSet<int> _cancellationAttempts;
    private readonly Func<Exception> _exceptionFactory;
    private readonly TimeSpan _delay;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource? _cancellationSource;
    private readonly ConcurrentQueue<AttemptRecord> _history = new();
    private int _attempt;

    internal DeterministicFaultInjector(
        IEnumerable<int>? failureAttempts = null,
        IEnumerable<int>? delayAttempts = null,
        IEnumerable<int>? cancellationAttempts = null,
        Func<Exception>? exceptionFactory = null,
        TimeSpan? delay = null,
        TimeProvider? timeProvider = null,
        CancellationTokenSource? cancellationSource = null)
    {
        _failureAttempts = failureAttempts?.ToHashSet() ?? [];
        _delayAttempts = delayAttempts?.ToHashSet() ?? [];
        _cancellationAttempts = cancellationAttempts?.ToHashSet() ?? [];
        _exceptionFactory = exceptionFactory ?? (() => new InjectedTransientException());
        _delay = delay ?? TimeSpan.Zero;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cancellationSource = cancellationSource;

        if (_cancellationAttempts.Count > 0 && _cancellationSource is null)
        {
            throw new ArgumentException(
                "A cancellation source is required when cancellation attempts are configured.",
                nameof(cancellationSource));
        }
    }

    internal static DeterministicFaultInjector FailFirst(
        int count,
        Func<Exception>? exceptionFactory = null) =>
        new(Enumerable.Range(1, count), exceptionFactory: exceptionFactory);

    internal IReadOnlyList<AttemptRecord> History => _history.OrderBy(item => item.Attempt).ToArray();

    internal int AttemptCount => Volatile.Read(ref _attempt);

    internal async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        int attempt = Interlocked.Increment(ref _attempt);
        _history.Enqueue(new AttemptRecord(attempt, "started"));

        if (_delayAttempts.Contains(attempt) && _delay > TimeSpan.Zero)
        {
            _history.Enqueue(new AttemptRecord(attempt, "delayed"));
            await Task.Delay(_delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        if (_cancellationAttempts.Contains(attempt))
        {
            _history.Enqueue(new AttemptRecord(attempt, "cancel-triggered"));
            _cancellationSource?.Cancel();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_failureAttempts.Contains(attempt))
        {
            _history.Enqueue(new AttemptRecord(attempt, "failed"));
            throw _exceptionFactory();
        }

        _history.Enqueue(new AttemptRecord(attempt, "succeeded"));
    }
}

internal readonly record struct AttemptRecord(int Attempt, string Outcome);
