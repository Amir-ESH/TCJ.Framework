using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Inbox;
using TCJ.Core.Resilience;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Inbox.Diagnostics;
using TCJ.EntityFrameworkCore.Inbox.Serialization;
using TCJ.EntityFrameworkCore.Inbox.Storage;

namespace TCJ.EntityFrameworkCore.Inbox.Processing;

internal sealed class InboxCoordinator<TDbContext> : IInboxPipeline, IInboxDeferredProcessor, IInboxReplayService, IInboxCleanupService
    where TDbContext : DbContext, IReadDbContext, IWriteDbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TcjInboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly InboxProcessorState _state;

    public InboxCoordinator(IServiceScopeFactory scopeFactory, TcjInboxOptions options, TimeProvider timeProvider, InboxProcessorState state)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _options.Validate();
    }

    public async Task<InboxHandlingResult> ProcessAsync(IncomingMessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEnvelope(envelope);

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        InboxMessageRegistry registry = services.GetRequiredService<InboxMessageRegistry>();
        bool registeredContract = registry.IsRegistered(envelope.MessageType, envelope.MessageVersion);
        string telemetryType = registeredContract ? envelope.MessageType : "unknown";
        int telemetryVersion = registeredContract ? envelope.MessageVersion : 0;
        IInboxStorage storage = services.GetRequiredService<IInboxStorage>();
        await services.GetRequiredService<IInboxStartupValidator>().ValidateAsync(cancellationToken).ConfigureAwait(false);

        using Activity? activity = InboxTelemetryDiagnostics.Start(
            TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.InboxReceive,
            "receive",
            _options.ConsumerName,
            telemetryType,
            telemetryVersion,
            provider: storage.ProviderName,
            headers: envelope.Headers);

        try
        {
            InboxHandlingResult result = _options.ProcessingMode == InboxProcessingMode.Deferred
                ? await StoreDeferredAsync(envelope, telemetryType, telemetryVersion, services, storage, cancellationToken).ConfigureAwait(false)
                : await ProcessInlineAsync(envelope, services, storage, registry, cancellationToken).ConfigureAwait(false);
            InboxTelemetryDiagnostics.Complete(activity, result.Outcome, result.FailureType);
            return result;
        }
        catch (OperationCanceledException)
        {
            InboxTelemetryDiagnostics.Complete(activity, InboxHandlingOutcome.Retry, InboxFailureType.Canceled);
            throw;
        }
    }

    public async Task<InboxProcessingResult> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state.MarkStarted();
        if (_options.ProcessingMode != InboxProcessingMode.Deferred)
        {
            _state.MarkSucceeded();
            return InboxProcessingResult.Empty;
        }

        try
        {
            await using AsyncServiceScope claimScope = _scopeFactory.CreateAsyncScope();
            IInboxStartupValidator validator = claimScope.ServiceProvider.GetRequiredService<IInboxStartupValidator>();
            await validator.ValidateAsync(cancellationToken).ConfigureAwait(false);
            IInboxStorage storage = claimScope.ServiceProvider.GetRequiredService<IInboxStorage>();
            IReadOnlyList<InboxMessage> claimed = await storage.ClaimBatchAsync(_timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            int processed = 0, retried = 0, dead = 0;
            foreach (InboxMessage message in claimed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InboxHandlingResult result = await ProcessClaimedAsync(message, cancellationToken).ConfigureAwait(false);
                if (result.Outcome == InboxHandlingOutcome.Acknowledge) processed++;
                else if (result.Outcome == InboxHandlingOutcome.Retry) retried++;
                else if (result.Outcome == InboxHandlingOutcome.DeadLetter) dead++;
            }
            _state.MarkSucceeded();
            return new InboxProcessingResult(claimed.Count, processed, retried, dead);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _state.MarkFailed(exception.GetType().Name);
            throw;
        }
    }

    public async Task<InboxReplayResult> ReplayAsync(Guid inboxId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInboxStartupValidator>().ValidateAsync(cancellationToken).ConfigureAwait(false);
        IInboxStorage storage = scope.ServiceProvider.GetRequiredService<IInboxStorage>();
        using Activity? activity = InboxTelemetryDiagnostics.Start(
            TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.InboxReplay,
            "replay",
            _options.ConsumerName,
            "administrative",
            0,
            provider: storage.ProviderName,
            kind: ActivityKind.Internal);
        bool replayed = await storage.ReplayAsync(inboxId, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        InboxTelemetryDiagnostics.Complete(activity, replayed ? InboxHandlingOutcome.Acknowledge : InboxHandlingOutcome.DeadLetter);
        return new InboxReplayResult(inboxId, replayed);
    }

    public async Task<InboxCleanupResult> CleanupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.RetentionPeriod == TimeSpan.Zero) return new InboxCleanupResult(0, true);
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInboxStartupValidator>().ValidateAsync(cancellationToken).ConfigureAwait(false);
        IInboxStorage storage = scope.ServiceProvider.GetRequiredService<IInboxStorage>();
        using Activity? activity = InboxTelemetryDiagnostics.Start(
            TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.InboxCleanup,
            "cleanup",
            _options.ConsumerName,
            "administrative",
            0,
            provider: storage.ProviderName,
            kind: ActivityKind.Internal);
        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - _options.RetentionPeriod;
        int deleted = await storage.CleanupAsync(cutoff, _options.CleanupBatchSize, cancellationToken).ConfigureAwait(false);
        InboxTelemetryDiagnostics.Complete(activity, InboxHandlingOutcome.Acknowledge);
        return new InboxCleanupResult(deleted, false);
    }

    private async Task<InboxHandlingResult> StoreDeferredAsync(IncomingMessageEnvelope envelope, string telemetryType, int telemetryVersion, IServiceProvider services, IInboxStorage storage, CancellationToken cancellationToken)
    {
        string payloadHash = HashPayload(envelope.Payload);
        string headers = SerializeAllowedHeaders(envelope);
        TDbContext dbContext = services.GetRequiredService<TDbContext>();
        await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        InboxStoreResult stored = await storage.StoreDeferredAsync(envelope, payloadHash, envelope.Payload, headers, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        InboxTelemetryDiagnostics.RecordReceived(_options.ConsumerName, telemetryType, telemetryVersion);
        if (stored.Kind == InboxAcquireKind.PayloadConflict)
        {
            InboxTelemetryDiagnostics.RecordFailure(_options.ConsumerName, "unknown", telemetryVersion, InboxFailureType.PayloadConflict, false);
            return new InboxHandlingResult(InboxHandlingOutcome.DeadLetter, 0, InboxFailureType.PayloadConflict, true);
        }
        if (stored.IsDuplicate)
        {
            InboxTelemetryDiagnostics.RecordDuplicate(_options.ConsumerName, telemetryType, telemetryVersion);
            InboxHandlingResult duplicate = stored.Kind == InboxAcquireKind.DeadLettered
                ? new InboxHandlingResult(InboxHandlingOutcome.DeadLetter, stored.Message?.AttemptCount ?? 0, ParseFailure(stored.Message?.LastErrorType), true)
                : new InboxHandlingResult(InboxHandlingOutcome.IgnoreDuplicate, stored.Message?.AttemptCount ?? 0, null, true);
            using Activity? duplicateActivity = InboxTelemetryDiagnostics.Start(
                TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.InboxDeduplicate,
                "deduplicate",
                _options.ConsumerName,
                telemetryType,
                telemetryVersion,
                duplicate.Attempt,
                storage.ProviderName);
            InboxTelemetryDiagnostics.Complete(duplicateActivity, duplicate.Outcome, duplicate.FailureType);
            return duplicate;
        }
        return new InboxHandlingResult(InboxHandlingOutcome.Acknowledge);
    }

    private async Task<InboxHandlingResult> ProcessInlineAsync(IncomingMessageEnvelope envelope, IServiceProvider services, IInboxStorage storage, InboxMessageRegistry registry, CancellationToken cancellationToken)
    {
        string payloadHash = HashPayload(envelope.Payload);
        string? storedPayload = _options.StorePayload ? envelope.Payload : null;
        string headers = SerializeAllowedHeaders(envelope);
        TDbContext dbContext = services.GetRequiredService<TDbContext>();
        Guid lockId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
        int attempt = 1;
        long started = Stopwatch.GetTimestamp();

        await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Activity? processActivity = null;
        try
        {
            InboxAcquireResult acquire = await storage.AcquireInlineAsync(envelope, payloadHash, storedPayload, headers, lockId, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            attempt = acquire.Attempt;
            if (acquire.Kind != InboxAcquireKind.Acquired)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                InboxHandlingResult duplicate = MapAcquire(acquire);
                bool duplicateContractRegistered = acquire.Kind != InboxAcquireKind.PayloadConflict && registry.IsRegistered(envelope.MessageType, envelope.MessageVersion);
                string duplicateType = duplicateContractRegistered ? envelope.MessageType : "unknown";
                int duplicateVersion = duplicateContractRegistered ? envelope.MessageVersion : 0;
                if (acquire.IsDuplicate) InboxTelemetryDiagnostics.RecordDuplicate(_options.ConsumerName, duplicateType, duplicateVersion);
                using Activity? duplicateActivity = InboxTelemetryDiagnostics.Start(
                    TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.InboxDeduplicate,
                    "deduplicate",
                    _options.ConsumerName,
                    duplicateType,
                    duplicateVersion,
                    duplicate.Attempt,
                    storage.ProviderName);
                InboxTelemetryDiagnostics.Complete(duplicateActivity, duplicate.Outcome, duplicate.FailureType);
                return duplicate;
            }

            processActivity = InboxTelemetryDiagnostics.Start(
                TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.InboxProcess,
                "process",
                _options.ConsumerName,
                envelope.MessageType,
                envelope.MessageVersion,
                attempt,
                storage.ProviderName);
            InboxResolvedRegistration registration = registry.Resolve(envelope.MessageType, envelope.MessageVersion);
            object message = services.GetRequiredService<IInboxSerializer>().Deserialize(registration.MessageType, envelope.Payload);
            var context = new InboxMessageContext(envelope.MessageId, _options.ConsumerName, envelope.MessageType, envelope.MessageVersion, attempt, envelope.CorrelationId, envelope.CausationId);
            InboxMessageContextAccessor accessor = services.GetRequiredService<InboxMessageContextAccessor>();
            using (accessor.Push(context))
            {
                await registration.Handler.Invoke(services, message, context, cancellationToken).ConfigureAwait(false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            await storage.MarkProcessedAsync(acquire.Message!.Id, lockId, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            InboxTelemetryDiagnostics.RecordReceived(_options.ConsumerName, envelope.MessageType, envelope.MessageVersion);
            InboxTelemetryDiagnostics.RecordProcessed(_options.ConsumerName, envelope.MessageType, envelope.MessageVersion, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            InboxTelemetryDiagnostics.Complete(processActivity, InboxHandlingOutcome.Acknowledge);
            return new InboxHandlingResult(InboxHandlingOutcome.Acknowledge, attempt);
        }
        catch (OperationCanceledException)
        {
            await SafeRollbackAsync(transaction).ConfigureAwait(false);
            InboxTelemetryDiagnostics.Complete(processActivity, InboxHandlingOutcome.Retry, InboxFailureType.Canceled);
            throw;
        }
        catch (Exception exception)
        {
            await SafeRollbackAsync(transaction).ConfigureAwait(false);
            InboxFailureType failure = Classify(exception, services.GetRequiredService<ITransientFailureDetector>());
            bool retry = IsRetryable(failure) && attempt <= _options.MaxRetryAttempts;
            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset? next = retry ? InboxRetrySchedule.Calculate(now, attempt, _options) : null;
            string safeError = CreateSafeError(failure);
            await using AsyncServiceScope failureScope = _scopeFactory.CreateAsyncScope();
            IInboxStorage failureStorage = failureScope.ServiceProvider.GetRequiredService<IInboxStorage>();
            await failureStorage.RecordInlineFailureAsync(envelope, payloadHash, storedPayload, headers, failure, safeError, retry, next, now, cancellationToken).ConfigureAwait(false);
            bool failureContractRegistered = failure is not (InboxFailureType.UnknownMessageType or InboxFailureType.UnknownMessageVersion) && registry.IsRegistered(envelope.MessageType, envelope.MessageVersion);
            string metricType = failureContractRegistered ? envelope.MessageType : "unknown";
            int metricVersion = failureContractRegistered ? envelope.MessageVersion : 0;
            InboxTelemetryDiagnostics.RecordFailure(_options.ConsumerName, metricType, metricVersion, failure, retry);
            InboxHandlingOutcome outcome = retry ? InboxHandlingOutcome.Retry : InboxHandlingOutcome.DeadLetter;
            InboxTelemetryDiagnostics.Complete(processActivity, outcome, failure);
            using Activity? failureActivity = InboxTelemetryDiagnostics.Start(
                retry ? TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.InboxRetry : TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.InboxDeadLetter,
                retry ? "retry" : "dead_letter",
                _options.ConsumerName,
                metricType,
                metricVersion,
                attempt,
                storage.ProviderName);
            InboxTelemetryDiagnostics.Complete(failureActivity, outcome, failure);
            return new InboxHandlingResult(outcome, attempt, failure);
        }
        finally
        {
            processActivity?.Dispose();
        }
    }

    private async Task<InboxHandlingResult> ProcessClaimedAsync(InboxMessage claimed, CancellationToken cancellationToken)
    {
        if (!claimed.LockId.HasValue) return new InboxHandlingResult(InboxHandlingOutcome.Retry, claimed.AttemptCount, InboxFailureType.ConcurrencyConflict);
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        IInboxStorage storage = services.GetRequiredService<IInboxStorage>();
        InboxMessageRegistry registry = services.GetRequiredService<InboxMessageRegistry>();
        TDbContext dbContext = services.GetRequiredService<TDbContext>();
        await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long started = Stopwatch.GetTimestamp();
        Activity? processActivity = null;
        try
        {
            InboxMessage? owned = await storage.LockClaimedAsync(claimed.Id, claimed.LockId.Value, cancellationToken).ConfigureAwait(false);
            if (owned is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new InboxHandlingResult(InboxHandlingOutcome.Retry, claimed.AttemptCount, InboxFailureType.ConcurrencyConflict);
            }
            if (owned.Payload is null) throw new InvalidOperationException("Deferred Inbox payload retention is required but the claimed record has no payload.");
            bool claimedContractRegistered = registry.IsRegistered(owned.MessageType, owned.MessageVersion);
            string claimedTelemetryType = claimedContractRegistered ? owned.MessageType : "unknown";
            int claimedTelemetryVersion = claimedContractRegistered ? owned.MessageVersion : 0;
            processActivity = InboxTelemetryDiagnostics.Start(
                TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.InboxProcess,
                "process",
                _options.ConsumerName,
                claimedTelemetryType,
                claimedTelemetryVersion,
                owned.AttemptCount,
                storage.ProviderName,
                DeserializeStoredHeaders(owned.HeadersJson));
            InboxResolvedRegistration registration = registry.Resolve(owned.MessageType, owned.MessageVersion);
            object message = services.GetRequiredService<IInboxSerializer>().Deserialize(registration.MessageType, owned.Payload);
            var context = new InboxMessageContext(owned.MessageId, owned.ConsumerName, owned.MessageType, owned.MessageVersion, owned.AttemptCount, owned.CorrelationId, owned.CausationId);
            using (services.GetRequiredService<InboxMessageContextAccessor>().Push(context))
            {
                await registration.Handler.Invoke(services, message, context, cancellationToken).ConfigureAwait(false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            await storage.MarkProcessedAsync(owned.Id, claimed.LockId.Value, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            InboxTelemetryDiagnostics.RecordProcessed(_options.ConsumerName, claimedTelemetryType, claimedTelemetryVersion, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            InboxTelemetryDiagnostics.Complete(processActivity, InboxHandlingOutcome.Acknowledge);
            return new InboxHandlingResult(InboxHandlingOutcome.Acknowledge, owned.AttemptCount);
        }
        catch (OperationCanceledException)
        {
            await SafeRollbackAsync(transaction).ConfigureAwait(false);
            InboxTelemetryDiagnostics.Complete(processActivity, InboxHandlingOutcome.Retry, InboxFailureType.Canceled);
            throw;
        }
        catch (Exception exception)
        {
            await SafeRollbackAsync(transaction).ConfigureAwait(false);
            InboxFailureType failure = Classify(exception, services.GetRequiredService<ITransientFailureDetector>());
            bool retry = IsRetryable(failure) && claimed.AttemptCount <= _options.MaxRetryAttempts;
            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset? next = retry ? InboxRetrySchedule.Calculate(now, claimed.AttemptCount, _options) : null;
            await using AsyncServiceScope failureScope = _scopeFactory.CreateAsyncScope();
            await failureScope.ServiceProvider.GetRequiredService<IInboxStorage>().ScheduleClaimedFailureAsync(claimed.Id, claimed.LockId.Value, claimed.AttemptCount, failure, CreateSafeError(failure), retry, next, now, cancellationToken).ConfigureAwait(false);
            bool failureContractRegistered = registry.IsRegistered(claimed.MessageType, claimed.MessageVersion);
            string metricType = failureContractRegistered ? claimed.MessageType : "unknown";
            int metricVersion = failureContractRegistered ? claimed.MessageVersion : 0;
            InboxTelemetryDiagnostics.RecordFailure(_options.ConsumerName, metricType, metricVersion, failure, retry);
            InboxHandlingOutcome outcome = retry ? InboxHandlingOutcome.Retry : InboxHandlingOutcome.DeadLetter;
            InboxTelemetryDiagnostics.Complete(processActivity, outcome, failure);
            using Activity? failureActivity = InboxTelemetryDiagnostics.Start(
                retry ? TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.InboxRetry : TCJ.Core.Diagnostics.TcjDiagnosticNames.Activities.InboxDeadLetter,
                retry ? "retry" : "dead_letter",
                _options.ConsumerName,
                metricType,
                metricVersion,
                claimed.AttemptCount,
                storage.ProviderName);
            InboxTelemetryDiagnostics.Complete(failureActivity, outcome, failure);
            return new InboxHandlingResult(outcome, claimed.AttemptCount, failure);
        }
        finally
        {
            processActivity?.Dispose();
        }
    }

    private void ValidateEnvelope(IncomingMessageEnvelope envelope)
    {
        if (!string.Equals(envelope.Consumer, _options.ConsumerName, StringComparison.Ordinal)) throw new ArgumentException($"Inbox envelope consumer '{envelope.Consumer}' does not match configured consumer boundary '{_options.ConsumerName}'.", nameof(envelope));
        int bytes = Encoding.UTF8.GetByteCount(envelope.Payload);
        if (bytes > _options.MaximumPayloadBytes) throw new ArgumentException($"Inbox payload exceeds the configured {_options.MaximumPayloadBytes}-byte limit.", nameof(envelope));
    }

    private string SerializeAllowedHeaders(IncomingMessageEnvelope envelope)
    {
        if (envelope.Headers.Count == 0) return "{}";
        var safe = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in envelope.Headers)
        {
            if (_options.HeaderAllowlist.Contains(key)) safe[key] = value;
        }
        return JsonSerializer.Serialize(safe);
    }

    private static IReadOnlyDictionary<string, string>? DeserializeStoredHeaders(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string HashPayload(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static InboxHandlingResult MapAcquire(InboxAcquireResult acquire) => acquire.Kind switch
    {
        InboxAcquireKind.ProcessedDuplicate => new InboxHandlingResult(InboxHandlingOutcome.IgnoreDuplicate, acquire.Attempt, null, true),
        InboxAcquireKind.PayloadConflict => new InboxHandlingResult(InboxHandlingOutcome.DeadLetter, acquire.Attempt, InboxFailureType.PayloadConflict, true),
        InboxAcquireKind.DeadLettered => new InboxHandlingResult(InboxHandlingOutcome.DeadLetter, acquire.Attempt, ParseFailure(acquire.Message?.LastErrorType), true),
        InboxAcquireKind.DuplicateInProgress or InboxAcquireKind.RetryNotDue => new InboxHandlingResult(InboxHandlingOutcome.Retry, acquire.Attempt, null, true),
        _ => new InboxHandlingResult(InboxHandlingOutcome.Retry, acquire.Attempt, InboxFailureType.ConcurrencyConflict, acquire.IsDuplicate)
    };

    private static InboxFailureType Classify(Exception exception, ITransientFailureDetector detector)
    {
        if (exception is InboxUnknownMessageTypeException) return InboxFailureType.UnknownMessageType;
        if (exception is InboxUnknownMessageVersionException) return InboxFailureType.UnknownMessageVersion;
        if (exception is JsonException) return InboxFailureType.PermanentDeserialization;
        if (exception is OperationCanceledException) return InboxFailureType.Canceled;
        if (exception is TimeoutException) return InboxFailureType.Timeout;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (detector.IsTransient(current)) return current is TimeoutException ? InboxFailureType.Timeout : InboxFailureType.TransientInfrastructure;
        }
        if (exception.GetType().Name.Contains("Validation", StringComparison.Ordinal)) return InboxFailureType.PermanentValidation;
        return InboxFailureType.Unhandled;
    }

    private static bool IsRetryable(InboxFailureType failure) => failure is InboxFailureType.TransientInfrastructure or InboxFailureType.TransientHandler or InboxFailureType.Timeout or InboxFailureType.ConcurrencyConflict;
    private string CreateSafeError(InboxFailureType failure)
    {
        string value = $"Inbox processing failed with bounded category {failure}. Payloads, exception messages, and stack traces are not persisted by default.";
        return value.Length <= _options.MaximumStoredErrorLength ? value : value[.._options.MaximumStoredErrorLength];
    }
    private static InboxFailureType? ParseFailure(string? value) => Enum.TryParse(value, ignoreCase: false, out InboxFailureType parsed) ? parsed : null;
    private static async Task SafeRollbackAsync(IDbContextTransaction transaction)
    {
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
    }
}
