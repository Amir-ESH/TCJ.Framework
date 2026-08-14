using System.Diagnostics;
using TCJ.Core.Diagnostics;

namespace TCJ.Core.Resilience;

/// <summary>
/// Applies a cooperative operation timeout while preserving the distinction
/// between caller cancellation and policy timeout.
/// </summary>
public sealed class TcjTimeoutPolicy
{
    private readonly TcjTimeoutOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a timeout policy from validated options.</summary>
    /// <param name="options">Timeout options.</param>
    /// <param name="timeProvider">Optional time provider used to schedule the timeout.</param>
    public TcjTimeoutPolicy(TcjTimeoutOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options.Clone();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Executes an asynchronous operation with the configured cooperative timeout.</summary>
    /// <param name="operation">Operation that must observe the provided cancellation token.</param>
    /// <param name="strategy">Stable low-cardinality strategy category used for telemetry.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>A task that completes when the operation completes or the timeout/caller cancellation is observed.</returns>
    public async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        string strategy = "operation_timeout",
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

    /// <summary>Executes an asynchronous operation and returns its result.</summary>
    /// <typeparam name="TResult">Operation result type.</typeparam>
    /// <param name="operation">Operation that must observe the provided cancellation token.</param>
    /// <param name="strategy">Stable low-cardinality strategy category used for telemetry.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>The successfully completed operation result.</returns>
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        string strategy = "operation_timeout",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategy);

        using Activity? executionActivity = ResilienceTelemetryDiagnostics.Start(
            TcjDiagnosticNames.Activities.ResilienceExecute,
            strategy);
        using var timeoutSource = new CancellationTokenSource(_options.OperationTimeout, _timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            TResult result = await operation(linkedSource.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (timeoutSource.IsCancellationRequested)
            {
                throw new OperationCanceledException(timeoutSource.Token);
            }

            ResilienceTelemetryDiagnostics.RecordAttempt(
                strategy,
                TcjDiagnosticNames.Outcomes.Success,
                1);
            TcjTelemetry.CompleteSuccess(executionActivity);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ResilienceTelemetryDiagnostics.RecordAttempt(
                strategy,
                TcjDiagnosticNames.Outcomes.Canceled,
                1,
                "canceled");
            TcjTelemetry.CompleteCanceled(executionActivity);
            throw;
        }
        catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested)
        {
            using Activity? timeoutActivity = ResilienceTelemetryDiagnostics.Start(
                TcjDiagnosticNames.Activities.ResilienceTimeout,
                strategy);
            timeoutActivity?.SetTag(TcjDiagnosticNames.Tags.ResilienceFailureType, "timeout");
            ResilienceTelemetryDiagnostics.RecordAttempt(strategy, "timeout", 1, "timeout");
            ResilienceTelemetryDiagnostics.RecordTimeout(strategy);
            ResilienceTelemetryDiagnostics.RecordFailure(strategy, "timeout");
            TcjTelemetry.CompleteFailure(timeoutActivity, exception);

            var timeoutException = new TcjTimeoutException(_options.OperationTimeout, exception);
            TcjTelemetry.CompleteFailure(executionActivity, timeoutException);
            throw timeoutException;
        }
        catch (OperationCanceledException)
        {
            ResilienceTelemetryDiagnostics.RecordAttempt(
                strategy,
                TcjDiagnosticNames.Outcomes.Canceled,
                1,
                "canceled");
            TcjTelemetry.CompleteCanceled(executionActivity);
            throw;
        }
        catch (Exception exception)
        {
            ResilienceTelemetryDiagnostics.RecordAttempt(
                strategy,
                TcjDiagnosticNames.Outcomes.Failure,
                1,
                "operation_failure");
            ResilienceTelemetryDiagnostics.RecordFailure(strategy, "operation_failure");
            TcjTelemetry.CompleteFailure(executionActivity, exception);
            throw;
        }
    }
}
