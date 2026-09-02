using System.Text.Json;
using TCJ.Core.DomainEvents;
using TCJ.Core.Outbox;
using TCJ.Core.Resilience;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Serialization;

namespace TCJ.Messaging.Integration;

internal sealed class MessagingOutboxDomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IDomainEventDispatcher _inner;
    private readonly IOutboxMessageContextAccessor _outboxContext;
    private readonly IMessageContractRegistry _contracts;
    private readonly IMessagePublisher _publisher;
    private readonly TcjMessagingOptions _options;
    private readonly TimeProvider _timeProvider;

    public MessagingOutboxDomainEventDispatcher(
        IDomainEventDispatcher inner,
        IOutboxMessageContextAccessor outboxContext,
        IMessageContractRegistry contracts,
        IMessagePublisher publisher,
        TcjMessagingOptions options,
        TimeProvider timeProvider)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _outboxContext = outboxContext ?? throw new ArgumentNullException(nameof(outboxContext));
        _contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options.Validate();
    }

    public async Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);
        cancellationToken.ThrowIfCancellationRequested();

        OutboxMessageContext? context = _outboxContext.Current;
        if (context is null)
        {
            await _inner.DispatchAsync(domainEvents, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (domainEvents.Count != 1)
            throw new MessagingOutboxPermanentException("OutboxBatchShape");

        IDomainEvent domainEvent = domainEvents.Single();
        MessagingMessageContract contract = ResolveContract(domainEvent.GetType(), context.EventType);
        byte[] body;
        try
        {
            body = JsonSerializer.SerializeToUtf8Bytes(domainEvent, contract.JsonTypeInfo);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new MessagingOutboxPermanentException("Serialization", exception);
        }

        if (body.Length > _options.MaximumPayloadBytes)
            throw new MessagingOutboxPermanentException("PayloadTooLarge");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (MessagingValidation.IsValidW3CTraceParent(context.TraceParent))
            headers["traceparent"] = context.TraceParent!;
        if (headers.ContainsKey("traceparent") && MessagingValidation.IsValidTraceState(context.TraceState))
            headers["tracestate"] = context.TraceState!;

        var envelope = new TransportMessageEnvelope(
            context.MessageId.ToString("D"),
            contract.MessageType,
            contract.MessageVersion,
            body,
            MessagingValidation.JsonContentType,
            _timeProvider.GetUtcNow(),
            context.CorrelationId,
            context.CausationId,
            headers: headers);

        PublishResult result = await _publisher.PublishAsync(envelope, new PublishContext(), cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
            return;
        if (result.Outcome == PublishOutcome.Canceled && cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        string failureType = NormalizeFailureType(result);
        if (result.IsRetryable)
            throw new MessagingOutboxTransientException(failureType);
        throw new MessagingOutboxPermanentException(failureType);
    }

    private MessagingMessageContract ResolveContract(Type eventType, string persistedEventType)
    {
        MessagingMessageContract[] candidates = _contracts.Contracts
            .Where(contract => contract.ClrType == eventType)
            .ToArray();
        if (candidates.Length == 0)
            throw new MessagingOutboxPermanentException("UnknownMessageType");
        if (candidates.Length == 1)
            return candidates[0];

        MessagingMessageContract[] exact = candidates
            .Where(contract => string.Equals(
                persistedEventType,
                $"{contract.MessageType}.v{contract.MessageVersion}",
                StringComparison.Ordinal))
            .ToArray();
        if (exact.Length == 1)
            return exact[0];
        throw new MessagingOutboxPermanentException("AmbiguousMessageContract");
    }

    private static string NormalizeFailureType(PublishResult result)
    {
        string value = result.FailureType ?? result.FailureCategory?.ToString() ?? result.Outcome.ToString();
        return value.Length <= 128 ? value : value[..128];
    }
}

internal sealed class MessagingOutboxTransientException : Exception
{
    public MessagingOutboxTransientException(string failureType)
        : base($"Messaging Outbox delivery failed transiently ({failureType}).") => FailureType = failureType;

    public string FailureType { get; }
}

internal sealed class MessagingOutboxPermanentException : Exception
{
    public MessagingOutboxPermanentException(string failureType, Exception? innerException = null)
        : base($"Messaging Outbox delivery failed permanently ({failureType}).", innerException) => FailureType = failureType;

    public string FailureType { get; }
}

internal sealed class MessagingOutboxTransientFailureClassifier : ITransientFailureClassifier
{
    public MessagingOutboxTransientFailureClassifier()
    {
    }

    public bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is MessagingOutboxTransientException;
    }
}
