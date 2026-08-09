using System.Diagnostics;
using TCJ.Core.Diagnostics;

namespace TCJ.Core.Resilience;

/// <summary>
/// Thread-safe, isolated circuit breaker for a single bounded operation category.
/// Instances must not be shared across unrelated endpoints, tenants, or dependencies.
/// </summary>
public sealed class TcjCircuitBreaker
{
    private readonly object _gate = new();
    private readonly ITransientFailureDetector _detector;
    private readonly TcjCircuitBreakerOptions _options;
    private readonly TimeProvider _timeProvider;
    private TcjCircuitState _state;
    private int _consecutiveTransientFailures;
    private DateTimeOffset _openUntil;
    private bool _halfOpenProbeActive;

    /// <summary>Creates an isolated circuit breaker.</summary>
    /// <param name="detector">Transient-failure detector used to decide which failures affect circuit state.</param>
    /// <param name="options">Validated circuit-breaker bounds.</param>
    /// <param name="timeProvider">Optional time provider used to control break duration.</param>
    public TcjCircuitBreaker(
        ITransientFailureDetector detector,
        TcjCircuitBreakerOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _detector = detector;
        _options = options.Clone();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the current circuit state.</summary>
    public TcjCircuitState State
    {
        get
        {
            lock (_gate)
            {
                RefreshOpenStateNoLock();
                return _state;
            }
        }
    }

    /// <summary>Executes an operation protected by this isolated circuit.</summary>
    /// <param name="operation">Operation to invoke when the circuit admits the call.</param>
    /// <param name="strategy">Stable low-cardinality strategy category used for telemetry.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>A task that completes when the protected operation completes.</returns>
    public async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        string strategy = "circuit_breaker",
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

    /// <summary>Executes an operation protected by this isolated circuit and returns its result.</summary>
    /// <typeparam name="TResult">Protected operation result type.</typeparam>
    /// <param name="operation">Operation to invoke when the circuit admits the call.</param>
    /// <param name="strategy">Stable low-cardinality strategy category used for telemetry.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>The result produced by the admitted operation.</returns>
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        string strategy = "circuit_breaker",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategy);
        cancellationToken.ThrowIfCancellationRequested();

        TcjCircuitState admittedState = AdmitOrThrow(strategy);
        using Activity? activity = ResilienceTelemetryDiagnostics.Start(
            TcjDiagnosticNames.Activities.ResilienceExecute,
            strategy);
        activity?.SetTag(
            TcjDiagnosticNames.Tags.ResilienceCircuitState,
            ToTelemetryState(admittedState));

        try
        {
            TResult result = await operation(cancellationToken).ConfigureAwait(false);
            CloseAfterSuccess();
            ResilienceTelemetryDiagnostics.RecordAttempt(
                strategy,
                TcjDiagnosticNames.Outcomes.Success,
                1);
            TcjTelemetry.CompleteSuccess(activity);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseCanceledProbe(admittedState);
            ResilienceTelemetryDiagnostics.RecordAttempt(
                strategy,
                TcjDiagnosticNames.Outcomes.Canceled,
                1,
                "canceled");
            TcjTelemetry.CompleteCanceled(activity);
            throw;
        }
        catch (OperationCanceledException)
        {
            ReleaseCanceledProbe(admittedState);
            ResilienceTelemetryDiagnostics.RecordAttempt(
                strategy,
                TcjDiagnosticNames.Outcomes.Canceled,
                1,
                "canceled");
            TcjTelemetry.CompleteCanceled(activity);
            throw;
        }
        catch (Exception exception)
        {
            bool transient = _detector.IsTransient(exception);
            string failureType = transient ? "transient" : "permanent";
            bool opened = RegisterFailure(admittedState, transient);

            ResilienceTelemetryDiagnostics.RecordAttempt(
                strategy,
                TcjDiagnosticNames.Outcomes.Failure,
                1,
                failureType);
            ResilienceTelemetryDiagnostics.RecordFailure(strategy, failureType);
            if (opened)
            {
                ResilienceTelemetryDiagnostics.RecordCircuitOpen(strategy);
                RecordCircuitActivity(strategy, TcjCircuitState.Open, failureType);
            }

            TcjTelemetry.CompleteFailure(activity, exception);
            throw;
        }
    }

    private TcjCircuitState AdmitOrThrow(string strategy)
    {
        lock (_gate)
        {
            RefreshOpenStateNoLock();

            if (_state == TcjCircuitState.Open)
            {
                RecordRejectedCall(strategy, TcjCircuitState.Open);
                throw new TcjCircuitOpenException(TcjCircuitState.Open);
            }

            if (_state == TcjCircuitState.HalfOpen)
            {
                if (_halfOpenProbeActive)
                {
                    RecordRejectedCall(strategy, TcjCircuitState.HalfOpen);
                    throw new TcjCircuitOpenException(TcjCircuitState.HalfOpen);
                }

                _halfOpenProbeActive = true;
            }

            return _state;
        }
    }

    private void RefreshOpenStateNoLock()
    {
        if (_state == TcjCircuitState.Open && _timeProvider.GetUtcNow() >= _openUntil)
        {
            _state = TcjCircuitState.HalfOpen;
            _halfOpenProbeActive = false;
        }
    }

    private void CloseAfterSuccess()
    {
        lock (_gate)
        {
            _state = TcjCircuitState.Closed;
            _consecutiveTransientFailures = 0;
            _halfOpenProbeActive = false;
            _openUntil = default;
        }
    }

    private void ReleaseCanceledProbe(TcjCircuitState admittedState)
    {
        if (admittedState != TcjCircuitState.HalfOpen)
        {
            return;
        }

        lock (_gate)
        {
            if (_state == TcjCircuitState.HalfOpen)
            {
                _halfOpenProbeActive = false;
            }
        }
    }

    private bool RegisterFailure(TcjCircuitState admittedState, bool transient)
    {
        lock (_gate)
        {
            if (!transient)
            {
                if (admittedState == TcjCircuitState.HalfOpen)
                {
                    _state = TcjCircuitState.Closed;
                    _halfOpenProbeActive = false;
                    _consecutiveTransientFailures = 0;
                }

                return false;
            }

            if (admittedState == TcjCircuitState.HalfOpen)
            {
                OpenNoLock();
                return true;
            }

            _consecutiveTransientFailures++;
            if (_consecutiveTransientFailures < _options.FailureThreshold)
            {
                return false;
            }

            OpenNoLock();
            return true;
        }
    }

    private void OpenNoLock()
    {
        _state = TcjCircuitState.Open;
        _consecutiveTransientFailures = 0;
        _halfOpenProbeActive = false;
        _openUntil = _timeProvider.GetUtcNow() + _options.BreakDuration;
    }

    private static void RecordRejectedCall(string strategy, TcjCircuitState state)
    {
        using Activity? activity = ResilienceTelemetryDiagnostics.Start(
            TcjDiagnosticNames.Activities.ResilienceCircuitBreaker,
            strategy);
        activity?.SetTag(TcjDiagnosticNames.Tags.ResilienceCircuitState, ToTelemetryState(state));
        activity?.SetTag(TcjDiagnosticNames.Tags.ResilienceFailureType, "circuit_open");
        TcjTelemetry.CompleteFailure(activity, new TcjCircuitOpenException(state));
        ResilienceTelemetryDiagnostics.RecordAttempt(
            strategy,
            TcjDiagnosticNames.Outcomes.Failure,
            1,
            "circuit_open");
        ResilienceTelemetryDiagnostics.RecordFailure(strategy, "circuit_open");
    }

    private static void RecordCircuitActivity(string strategy, TcjCircuitState state, string failureType)
    {
        using Activity? activity = ResilienceTelemetryDiagnostics.Start(
            TcjDiagnosticNames.Activities.ResilienceCircuitBreaker,
            strategy);
        activity?.SetTag(TcjDiagnosticNames.Tags.ResilienceCircuitState, ToTelemetryState(state));
        activity?.SetTag(TcjDiagnosticNames.Tags.ResilienceFailureType, failureType);
        TcjTelemetry.CompleteSuccess(activity);
    }

    private static string ToTelemetryState(TcjCircuitState state) => state switch
    {
        TcjCircuitState.Closed => "closed",
        TcjCircuitState.Open => "open",
        TcjCircuitState.HalfOpen => "half_open",
        _ => "unknown"
    };
}
