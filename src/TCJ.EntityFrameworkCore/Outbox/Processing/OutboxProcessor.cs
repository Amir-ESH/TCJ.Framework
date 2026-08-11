using System.Diagnostics;
using TCJ.Core.DomainEvents;
using TCJ.Core.Outbox;
using TCJ.Core.Resilience;
using TCJ.EntityFrameworkCore.Outbox.Diagnostics;

namespace TCJ.EntityFrameworkCore.Outbox.Processing;

internal sealed class OutboxProcessor : IOutboxProcessor, IOutboxReplayService, IOutboxCleanupService
{
    private readonly IOutboxStorage _storage;
    private readonly IOutboxSerializer _serializer;
    private readonly IOutboxEventTypeResolver _eventTypeResolver;
    private readonly IDomainEventDispatcher _dispatcher;
    private readonly ITransientFailureDetector _failureDetector;
    private readonly OutboxMessageContextAccessor _contextAccessor;
    private readonly IOutboxStartupValidator _startupValidator;
    private readonly OutboxProcessorState _state;
    private readonly TcjOutboxOptions _options;
    private readonly TimeProvider _timeProvider;

    public OutboxProcessor(
        IOutboxStorage storage,
        IOutboxSerializer serializer,
        IOutboxEventTypeResolver eventTypeResolver,
        IDomainEventDispatcher dispatcher,
        ITransientFailureDetector failureDetector,
        OutboxMessageContextAccessor contextAccessor,
        IOutboxStartupValidator startupValidator,
        OutboxProcessorState state,
        TcjOutboxOptions options,
        TimeProvider timeProvider)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _eventTypeResolver = eventTypeResolver ?? throw new ArgumentNullException(nameof(eventTypeResolver));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _failureDetector = failureDetector ?? throw new ArgumentNullException(nameof(failureDetector));
        _contextAccessor = contextAccessor ?? throw new ArgumentNullException(nameof(contextAccessor));
        _startupValidator = startupValidator ?? throw new ArgumentNullException(nameof(startupValidator));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options.Validate();
    }

    public async Task<OutboxProcessingResult> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state.MarkStarted();

        try
        {
            await _startupValidator.ValidateAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset claimTime = _timeProvider.GetUtcNow();
            using Activity? claimActivity = OutboxTelemetryDiagnostics.Start(
                TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.OutboxClaim,
                "claim",
                provider: _storage.ProviderName);

            IReadOnlyList<OutboxMessage> messages;
            try
            {
                messages = await _storage.ClaimBatchAsync(claimTime, cancellationToken).ConfigureAwait(false);
                OutboxTelemetryDiagnostics.CompleteSuccess(claimActivity);
            }
            catch (OperationCanceledException)
            {
                OutboxTelemetryDiagnostics.CompleteCanceled(claimActivity);
                throw;
            }
            catch (Exception exception)
            {
                OutboxTelemetryDiagnostics.CompleteFailure(claimActivity, exception);
                throw;
            }

            int processed = 0;
            int retried = 0;
            int deadLettered = 0;

            foreach (OutboxMessage message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guid lockId = message.LockId ?? throw new InvalidOperationException("Claimed outbox records must have a lock identifier.");
                int attempt = checked(message.AttemptCount + 1);
                long started = Stopwatch.GetTimestamp();
                using Activity? activity = OutboxTelemetryDiagnostics.Start(
                    TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.OutboxProcess,
                    "process",
                    message.EventType,
                    attempt,
                    _storage.ProviderName);

                try
                {
                    Type eventType = _eventTypeResolver.Resolve(message.EventType);
                    IDomainEvent domainEvent = _serializer.Deserialize(eventType, message.Payload);
                    using IDisposable scope = _contextAccessor.Push(new OutboxMessageContext(message.Id, message.EventType, attempt));
                    await _dispatcher.DispatchAsync([domainEvent], cancellationToken).ConfigureAwait(false);
                    DateTimeOffset completedAt = _timeProvider.GetUtcNow();
                    await _storage.MarkProcessedAsync(message.Id, lockId, attempt, completedAt, cancellationToken).ConfigureAwait(false);
                    processed++;
                    OutboxTelemetryDiagnostics.RecordProcessed(message.EventType, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    OutboxTelemetryDiagnostics.CompleteSuccess(activity);
                }
                catch (OperationCanceledException)
                {
                    OutboxTelemetryDiagnostics.CompleteCanceled(activity);
                    throw;
                }
                catch (OutboxLeaseLostException exception)
                {
                    // A different worker may now own the message. Do not write failure state using a stale lease.
                    OutboxTelemetryDiagnostics.CompleteFailure(activity, exception);
                }
                catch (Exception exception)
                {
                    DateTimeOffset failedAt = _timeProvider.GetUtcNow();
                    string errorType = OutboxTelemetryDiagnostics.NormalizeType(exception.GetType());
                    string safeError = CreateSafeError(errorType, _options.MaximumStoredErrorLength);
                    bool canRetry = _failureDetector.IsTransient(exception) && attempt <= _options.MaxRetryAttempts;

                    if (canRetry)
                    {
                        using Activity? retryActivity = OutboxTelemetryDiagnostics.Start(
                            TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.OutboxRetry,
                            "retry",
                            message.EventType,
                            attempt,
                            _storage.ProviderName);
                        try
                        {
                            TimeSpan delay = OutboxRetrySchedule.GetDelay(message.Id, attempt, _options);
                            DateTimeOffset nextAttempt = failedAt + delay;
                            await _storage.ScheduleRetryAsync(
                                message.Id,
                                lockId,
                                attempt,
                                nextAttempt,
                                errorType,
                                safeError,
                                failedAt,
                                cancellationToken).ConfigureAwait(false);
                            retried++;
                            OutboxTelemetryDiagnostics.RecordFailure(message.EventType, attempt, retryScheduled: true);
                            OutboxTelemetryDiagnostics.CompleteSuccess(retryActivity);
                        }
                        catch (OperationCanceledException)
                        {
                            OutboxTelemetryDiagnostics.CompleteCanceled(retryActivity);
                            throw;
                        }
                        catch (Exception retryException)
                        {
                            OutboxTelemetryDiagnostics.CompleteFailure(retryActivity, retryException);
                            throw;
                        }
                    }
                    else
                    {
                        using Activity? deadLetterActivity = OutboxTelemetryDiagnostics.Start(
                            TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.OutboxDeadLetter,
                            "dead_letter",
                            message.EventType,
                            attempt,
                            _storage.ProviderName);
                        try
                        {
                            await _storage.DeadLetterAsync(
                                message.Id,
                                lockId,
                                attempt,
                                errorType,
                                safeError,
                                failedAt,
                                cancellationToken).ConfigureAwait(false);
                            deadLettered++;
                            OutboxTelemetryDiagnostics.RecordFailure(message.EventType, attempt, retryScheduled: false);
                            OutboxTelemetryDiagnostics.CompleteSuccess(deadLetterActivity);
                        }
                        catch (OperationCanceledException)
                        {
                            OutboxTelemetryDiagnostics.CompleteCanceled(deadLetterActivity);
                            throw;
                        }
                        catch (Exception deadLetterException)
                        {
                            OutboxTelemetryDiagnostics.CompleteFailure(deadLetterActivity, deadLetterException);
                            throw;
                        }
                    }

                    OutboxTelemetryDiagnostics.CompleteFailure(activity, exception);
                }
            }

            DateTimeOffset succeededAt = _timeProvider.GetUtcNow();
            _state.MarkSucceeded(succeededAt);
            return new OutboxProcessingResult(messages.Count, processed, retried, deadLettered);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _state.MarkFailed(OutboxTelemetryDiagnostics.NormalizeType(exception.GetType()));
            throw;
        }
    }

    public async Task<OutboxReplayResult> ReplayAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _startupValidator.ValidateAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        using Activity? activity = OutboxTelemetryDiagnostics.Start(
            TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.OutboxReplay,
            "replay",
            provider: _storage.ProviderName);
        try
        {
            bool replayed = await _storage.ReplayAsync(messageId, now, cancellationToken).ConfigureAwait(false);
            OutboxTelemetryDiagnostics.CompleteSuccess(activity);
            return new OutboxReplayResult(messageId, replayed);
        }
        catch (OperationCanceledException)
        {
            OutboxTelemetryDiagnostics.CompleteCanceled(activity);
            throw;
        }
        catch (Exception exception)
        {
            OutboxTelemetryDiagnostics.CompleteFailure(activity, exception);
            throw;
        }
    }

    public async Task<OutboxCleanupResult> CleanupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.RetentionPeriod == TimeSpan.Zero)
        {
            return new OutboxCleanupResult(0, RetentionDisabled: true);
        }

        await _startupValidator.ValidateAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset cutoff = now - _options.RetentionPeriod;
        using Activity? activity = OutboxTelemetryDiagnostics.Start(
            TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.OutboxCleanup,
            "cleanup",
            provider: _storage.ProviderName);
        try
        {
            int deleted = await _storage.CleanupAsync(cutoff, _options.CleanupBatchSize, cancellationToken).ConfigureAwait(false);
            OutboxTelemetryDiagnostics.CompleteSuccess(activity);
            return new OutboxCleanupResult(deleted, RetentionDisabled: false);
        }
        catch (OperationCanceledException)
        {
            OutboxTelemetryDiagnostics.CompleteCanceled(activity);
            throw;
        }
        catch (Exception exception)
        {
            OutboxTelemetryDiagnostics.CompleteFailure(activity, exception);
            throw;
        }
    }

    private static string CreateSafeError(string errorType, int maximumLength)
    {
        string message = $"Outbox delivery failed with {errorType}. Exception messages and stack traces are not persisted by default.";
        return message.Length <= maximumLength ? message : message[..maximumLength];
    }
}
