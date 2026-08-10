using System.Diagnostics;
using TCJ.Core.Diagnostics;

namespace TCJ.Core.Resilience;

/// <summary>
/// Executes an explicitly retryable operation with bounded exponential backoff,
/// optional jitter, cancellation propagation, and backend-neutral telemetry.
/// </summary>
public sealed class TcjRetryPolicy
{
    private readonly ITransientFailureDetector _detector;
    private readonly TcjRetryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private readonly object _randomGate = new();

    /// <summary>Creates a retry policy from validated options.</summary>
    /// <param name="detector">Transient-failure detector.</param>
    /// <param name="options">Retry options.</param>
    /// <param name="timeProvider">Optional time provider used for retry delays.</param>
    public TcjRetryPolicy(
        ITransientFailureDetector detector,
        TcjRetryOptions options,
        TimeProvider? timeProvider = null)
        : this(detector, options, timeProvider ?? TimeProvider.System, Random.Shared)
    {
    }

    internal TcjRetryPolicy(
        ITransientFailureDetector detector,
        TcjRetryOptions options,
        TimeProvider timeProvider,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(random);

        options.Validate();
        _detector = detector;
        _options = options.Clone();
        _timeProvider = timeProvider;
        _random = random;
    }

    /// <summary>Executes a retryable asynchronous operation.</summary>
    /// <param name="operation">Operation invoked once plus at most the configured retry count.</param>
    /// <param name="strategy">A stable, low-cardinality strategy name used by telemetry.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>A task that completes when the operation succeeds or a terminal failure is propagated.</returns>
    public async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        string strategy = "operation",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAsync<object?>(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return null;
            },
            strategy,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Executes a retryable asynchronous operation and returns its result.</summary>
    /// <typeparam name="TResult">Operation result type.</typeparam>
    /// <param name="operation">Operation invoked once plus at most the configured retry count.</param>
    /// <param name="strategy">A stable, low-cardinality strategy name used by telemetry.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>The successful result.</returns>
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        string strategy = "operation",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateStrategy(strategy);

        using Activity? executionActivity = ResilienceTelemetryDiagnostics.Start(
            TcjDiagnosticNames.Activities.ResilienceExecute,
            strategy);

        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                TResult result = await operation(cancellationToken).ConfigureAwait(false);
                ResilienceTelemetryDiagnostics.RecordAttempt(
                    strategy,
                    TcjDiagnosticNames.Outcomes.Success,
                    attempt);
                TcjTelemetry.CompleteSuccess(executionActivity);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ResilienceTelemetryDiagnostics.RecordAttempt(
                    strategy,
                    TcjDiagnosticNames.Outcomes.Canceled,
                    attempt,
                    "canceled");
                TcjTelemetry.CompleteCanceled(executionActivity);
                throw;
            }
            catch (OperationCanceledException)
            {
                ResilienceTelemetryDiagnostics.RecordAttempt(
                    strategy,
                    TcjDiagnosticNames.Outcomes.Canceled,
                    attempt,
                    "canceled");
                TcjTelemetry.CompleteCanceled(executionActivity);
                throw;
            }
            catch (Exception exception)
            {
                bool transient = _detector.IsTransient(exception);
                string failureType = transient ? "transient" : "permanent";
                ResilienceTelemetryDiagnostics.RecordAttempt(
                    strategy,
                    TcjDiagnosticNames.Outcomes.Failure,
                    attempt,
                    failureType);

                int retriesUsed = attempt - 1;
                if (!transient || retriesUsed >= _options.MaxRetryAttempts)
                {
                    ResilienceTelemetryDiagnostics.RecordFailure(strategy, failureType);
                    TcjTelemetry.CompleteFailure(executionActivity, exception);
                    throw;
                }

                int nextAttempt = attempt + 1;
                TimeSpan delay = GetDelay(retriesUsed + 1);
                using Activity? retryActivity = ResilienceTelemetryDiagnostics.Start(
                    TcjDiagnosticNames.Activities.ResilienceRetry,
                    strategy);
                retryActivity?.SetTag(TcjDiagnosticNames.Tags.ResilienceAttempt, nextAttempt);
                retryActivity?.SetTag(TcjDiagnosticNames.Tags.ResilienceFailureType, failureType);
                ResilienceTelemetryDiagnostics.RecordRetry(strategy, nextAttempt, failureType);

                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                    }

                    TcjTelemetry.CompleteSuccess(retryActivity);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TcjTelemetry.CompleteCanceled(retryActivity);
                    TcjTelemetry.CompleteCanceled(executionActivity);
                    throw;
                }
            }
        }
    }

    internal TimeSpan GetDelay(int retryNumber)
    {
        if (retryNumber <= 0 || _options.BaseDelay == TimeSpan.Zero || _options.MaxDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        double exponent = Math.Pow(2, retryNumber - 1);
        double boundedTicks = Math.Min(_options.MaxDelay.Ticks, _options.BaseDelay.Ticks * exponent);

        if (_options.UseJitter && boundedTicks > 0)
        {
            double sample;
            lock (_randomGate)
            {
                sample = _random.NextDouble();
            }

            // Equal jitter: retain half the deterministic delay and randomize the other half.
            boundedTicks *= 0.5d + (sample * 0.5d);
        }

        long ticks = Math.Clamp((long)boundedTicks, 0L, _options.MaxDelay.Ticks);
        return TimeSpan.FromTicks(ticks);
    }

    private static void ValidateStrategy(string strategy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategy);
        if (strategy.Length > 64)
        {
            throw new ArgumentException("Resilience strategy names must be 64 characters or fewer.", nameof(strategy));
        }
    }
}
