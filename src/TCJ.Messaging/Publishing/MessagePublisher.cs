using System.Diagnostics;
using TCJ.Core.Inbox;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Diagnostics;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Topology;

namespace TCJ.Messaging.Publishing;

internal sealed record PreparedPublish(TransportMessageEnvelope Message, PublishContext Context, string Destination);

/// <summary>Central policy-enforcing broker-neutral publisher.</summary>
public sealed class MessagePublisher : IMessagePublisher
{
    private readonly IMessagingTransportPublisher _transport;
    private readonly MessagingTransportDescriptor _descriptor;
    private readonly IMessageTopologyNamingStrategy _topology;
    private readonly MessagingHeaderPolicy _headers;
    private readonly TcjMessagingOptions _options;
    private readonly IInboxMessageContextAccessor? _inboxContext;
    private readonly IMessagingStartupValidator _startupValidator;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the policy-enforcing publisher.</summary>
    /// <param name="transport">Concrete transport publisher.</param><param name="descriptor">Transport capabilities.</param>
    /// <param name="topology">Topology strategy.</param><param name="headers">Header policy.</param><param name="options">Messaging options.</param>
    /// <param name="inboxContexts">Optional Inbox context accessors.</param><param name="startupValidator">Startup validator.</param>
    /// <param name="timeProvider">Time source used for deterministic publish timeouts.</param>
    public MessagePublisher(IMessagingTransportPublisher transport, MessagingTransportDescriptor descriptor,
        IMessageTopologyNamingStrategy topology, MessagingHeaderPolicy headers, TcjMessagingOptions options,
        IEnumerable<IInboxMessageContextAccessor> inboxContexts,
        IMessagingStartupValidator startupValidator,
        TimeProvider timeProvider)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _headers = headers ?? throw new ArgumentNullException(nameof(headers));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _startupValidator = startupValidator ?? throw new ArgumentNullException(nameof(startupValidator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        IInboxMessageContextAccessor[] contexts = inboxContexts?.Take(2).ToArray() ?? throw new ArgumentNullException(nameof(inboxContexts));
        if (contexts.Length > 1) throw new InvalidOperationException("Only one Inbox message-context accessor may participate in messaging propagation.");
        _inboxContext = contexts.SingleOrDefault();
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<PublishResult> PublishAsync(TransportMessageEnvelope message, PublishContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        await _startupValidator.ValidateAsync(cancellationToken).ConfigureAwait(false);
        PublishResult? preparationFailure = TryPrepare(message, context, out PreparedPublish? prepared);
        if (preparationFailure is not null) return preparationFailure;
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = MessagingDiagnostics.StartPublish(prepared!.Message, _descriptor, prepared.Destination);
        using var transportCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<PublishResult> publishTask = _transport.PublishAsync(prepared.Message, prepared.Context, transportCts.Token);
        PublishResult result;
        try
        {
            result = await publishTask
                .WaitAsync(_options.PublishTimeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            transportCts.Cancel();
            _ = ObserveDetachedAsync(publishTask);
            result = new PublishResult(PublishOutcome.Canceled, FailureCategory: MessagingFailureCategory.Canceled, FailureType: nameof(MessagingFailureCategory.Canceled));
        }
        catch (TimeoutException)
        {
            transportCts.Cancel();
            _ = ObserveDetachedAsync(publishTask);
            result = new PublishResult(PublishOutcome.TimedOut, FailureCategory: MessagingFailureCategory.TransientTimeout, FailureType: nameof(MessagingFailureCategory.TransientTimeout));
        }
        MessagingDiagnostics.CompletePublish(activity, result, Stopwatch.GetElapsedTime(started).TotalMilliseconds, _descriptor, prepared.Destination, prepared.Message);
        return result;
    }

    internal PublishResult? TryPrepare(TransportMessageEnvelope message, PublishContext context, out PreparedPublish? prepared)
    {
        prepared = null;
        try
        {
            MessagingValidation.ValidateIdentifier(message.MessageId, nameof(message.MessageId), _options.MaximumMessageIdLength);
            MessagingValidation.ValidateMessageType(message.MessageType, nameof(message.MessageType), _options.MaximumMessageTypeLength);
            MessagingValidation.ValidateVersion(message.MessageVersion, nameof(message.MessageVersion));
            if (message.Body.Length > Math.Min(_options.MaximumPayloadBytes, _descriptor.Capabilities.MaximumPayloadBytes ?? int.MaxValue))
                return new PublishResult(PublishOutcome.PermanentFailure, FailureCategory: MessagingFailureCategory.PayloadTooLarge, FailureType: nameof(MessagingFailureCategory.PayloadTooLarge));

            string destination = string.IsNullOrWhiteSpace(context.Destination)
                ? _topology.GetDestination(message.MessageType, message.MessageVersion)
                : context.Destination!;
            MessagingValidation.ValidateTopologyName(destination, nameof(context.Destination), _options.MaximumDestinationNameLength);
            if (context.ScheduledAtUtc is not null && !_descriptor.Capabilities.SupportsScheduling) return PublishResult.Unsupported("Scheduling");
            if (context.TimeToLive is not null && !_descriptor.Capabilities.SupportsTimeToLive) return PublishResult.Unsupported("TimeToLive");
            string? partition = context.PartitionKey ?? message.PartitionKey;
            string? ordering = context.OrderingKey ?? message.OrderingKey;
            if (partition is not null && !_descriptor.Capabilities.SupportsPartitioning) return PublishResult.Unsupported("Partitioning");
            if (ordering is not null && !_descriptor.Capabilities.SupportsOrderedDelivery) return PublishResult.Unsupported("OrderedDelivery");
            if (context.TimeToLive is { } ttl && ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(context.TimeToLive));

            var rawHeaders = new Dictionary<string, string>(message.Headers, StringComparer.OrdinalIgnoreCase)
            {
                ["tcj-message-id"] = message.MessageId,
                ["tcj-message-type"] = message.MessageType,
                ["tcj-message-version"] = message.MessageVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["tcj-created-at"] = message.CreatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["content-type"] = message.ContentType
            };
            string? correlation = message.CorrelationId;
            string? causation = message.CausationId;
            InboxMessageContext? inbox = _inboxContext?.Current;
            correlation ??= inbox?.CorrelationId;
            causation ??= inbox?.MessageId;
            if (correlation is not null) rawHeaders["tcj-correlation-id"] = correlation;
            if (causation is not null) rawHeaders["tcj-causation-id"] = causation;
            Activity? current = Activity.Current;
            if (!rawHeaders.ContainsKey("traceparent") && current is { IdFormat: ActivityIdFormat.W3C, Id: not null })
                rawHeaders["traceparent"] = current.Id;
            if (!rawHeaders.ContainsKey("tracestate") && current?.TraceStateString is { Length: > 0 } traceState)
                rawHeaders["tracestate"] = traceState;
            IReadOnlyDictionary<string, string> filtered = _headers.Filter(rawHeaders);
            int adapterHeaderMax = _descriptor.Capabilities.MaximumHeaderBytes ?? int.MaxValue;
            if (MessagingValidation.GetHeaderByteCount(filtered) > adapterHeaderMax)
                return new PublishResult(PublishOutcome.PermanentFailure, FailureCategory: MessagingFailureCategory.PayloadTooLarge, FailureType: "HeaderLimit");

            TransportMessageEnvelope effective = message.WithMetadata(correlation, causation, filtered);
            var effectiveContext = context with { Destination = destination, PartitionKey = partition, OrderingKey = ordering };
            prepared = new PreparedPublish(effective, effectiveContext, destination);
            return null;
        }
        catch (ArgumentException)
        {
            return new PublishResult(PublishOutcome.PermanentFailure, FailureCategory: MessagingFailureCategory.PermanentSerialization, FailureType: nameof(MessagingFailureCategory.PermanentSerialization));
        }
    }
    private static async Task ObserveDetachedAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

}
