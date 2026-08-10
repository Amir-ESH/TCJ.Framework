namespace TCJ.Core.HealthChecks;

internal sealed class AsyncHealthCheckCache<T>(TimeProvider timeProvider, TimeSpan duration)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly TimeSpan _duration = duration;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly object _sync = new();
    private bool _hasValue;
    private T? _value;
    private DateTimeOffset _expiresAt;

    internal async Task<T> GetOrCreateAsync(Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (TryGetFresh(out T? cached))
        {
            return cached!;
        }

        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetFresh(out cached))
            {
                return cached!;
            }

            T value = await factory(cancellationToken).ConfigureAwait(false);
            if (_duration > TimeSpan.Zero)
            {
                lock (_sync)
                {
                    _value = value;
                    _expiresAt = _timeProvider.GetUtcNow().Add(_duration);
                    _hasValue = true;
                }
            }

            return value;
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    internal void Invalidate()
    {
        lock (_sync)
        {
            _hasValue = false;
            _value = default;
            _expiresAt = default;
        }
    }

    private bool TryGetFresh(out T? value)
    {
        lock (_sync)
        {
            if (_hasValue && _timeProvider.GetUtcNow() < _expiresAt)
            {
                value = _value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
