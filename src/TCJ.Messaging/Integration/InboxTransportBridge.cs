using System.Text;
using TCJ.Core.Inbox;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Diagnostics;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.Integration;

/// <summary>Result of transactional Inbox processing and the transport settlement chosen afterward.</summary>
/// <param name="InboxResult">Committed Inbox outcome.</param>
/// <param name="Settlement">Transport settlement executed after the Inbox result.</param>
public sealed record InboxTransportBridgeResult(InboxHandlingResult InboxResult, MessageSettlement Settlement);

/// <summary>Maps received transport envelopes into the transactional Inbox and settles only after Inbox completion.</summary>
public sealed class InboxTransportBridge
{
    private readonly IInboxPipeline _inbox;
    private readonly TcjInboxOptions _inboxOptions;
    private readonly TcjMessagingOptions _messagingOptions;
    private readonly MessagingTransportDescriptor _descriptor;
    private readonly MessagingHeaderPolicy _headerPolicy;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the transport-to-Inbox bridge.</summary>
    /// <param name="inbox">Transactional Inbox pipeline.</param>
    /// <param name="inboxOptions">Inbox contract and payload options.</param>
    /// <param name="messagingOptions">Messaging validation and settlement options.</param>
    /// <param name="descriptor">Selected transport descriptor and capabilities.</param>
    /// <param name="headerPolicy">Inbound header allowlist and sanitization policy.</param>
    /// <param name="timeProvider">Time source used for deterministic receive metadata.</param>
    public InboxTransportBridge(IInboxPipeline inbox, TcjInboxOptions inboxOptions, TcjMessagingOptions messagingOptions,
        MessagingTransportDescriptor descriptor, MessagingHeaderPolicy headerPolicy, TimeProvider timeProvider)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _inboxOptions = inboxOptions ?? throw new ArgumentNullException(nameof(inboxOptions));
        _messagingOptions = messagingOptions ?? throw new ArgumentNullException(nameof(messagingOptions));
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _headerPolicy = headerPolicy ?? throw new ArgumentNullException(nameof(headerPolicy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _inboxOptions.Validate();
        _messagingOptions.Validate();
    }

    /// <summary>Processes one received delivery and performs settlement strictly after Inbox processing returns.</summary>
    /// <param name="message">Received transport delivery.</param><param name="cancellationToken">Caller token.</param>
    /// <returns>Committed Inbox outcome and executed settlement.</returns>
    public async Task<InboxTransportBridgeResult> ProcessAsync(ReceivedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            MessagingValidation.ValidateJsonContentType(message.Envelope.ContentType, nameof(message.Envelope.ContentType));
        }
        catch (NotSupportedException)
        {
            var unsupportedContentType = new InboxHandlingResult(
                InboxHandlingOutcome.DeadLetter,
                message.Delivery.DeliveryAttempt,
                InboxFailureType.PermanentDeserialization);
            return await SettleAsync(message, unsupportedContentType, cancellationToken).ConfigureAwait(false);
        }

        if (message.Envelope.Body.Length > Math.Min(_messagingOptions.MaximumPayloadBytes, _inboxOptions.MaximumPayloadBytes))
        {
            var oversized = new InboxHandlingResult(InboxHandlingOutcome.DeadLetter, message.Delivery.DeliveryAttempt, InboxFailureType.PermanentValidation);
            return await SettleAsync(message, oversized, cancellationToken).ConfigureAwait(false);
        }

        string payload;
        try { payload = new UTF8Encoding(false, true).GetString(message.Envelope.Body.Span); }
        catch (DecoderFallbackException)
        {
            var invalidUtf8 = new InboxHandlingResult(InboxHandlingOutcome.DeadLetter, message.Delivery.DeliveryAttempt, InboxFailureType.PermanentDeserialization);
            return await SettleAsync(message, invalidUtf8, cancellationToken).ConfigureAwait(false);
        }
        IReadOnlyDictionary<string, string> safeHeaders = _headerPolicy.Filter(message.Envelope.Headers);
        var incoming = new IncomingMessageEnvelope(
            message.Envelope.MessageId,
            message.Envelope.MessageType,
            message.Envelope.MessageVersion,
            _inboxOptions.ConsumerName,
            payload,
            message.Delivery.ReceivedAtUtc,
            message.Envelope.CorrelationId,
            message.Envelope.CausationId,
            safeHeaders);

        InboxHandlingResult result = await _inbox.ProcessAsync(incoming, cancellationToken).ConfigureAwait(false);
        // IInboxPipeline returns only after its configured persistence/transaction boundary has reached this outcome.
        return await SettleAsync(message, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InboxTransportBridgeResult> SettleAsync(ReceivedMessage message, InboxHandlingResult result, CancellationToken cancellationToken)
    {
        MessageSettlement settlement;
        using System.Diagnostics.Activity? activity = MessagingDiagnostics.StartSettle(_descriptor, result.Outcome.ToString());
        switch (result.Outcome)
        {
            case InboxHandlingOutcome.Acknowledge:
            case InboxHandlingOutcome.IgnoreDuplicate:
                await message.Settlement.CompleteAsync(cancellationToken).ConfigureAwait(false);
                settlement = MessageSettlement.Complete;
                break;
            case InboxHandlingOutcome.Retry:
                await message.Settlement.RetryAsync(new RetrySettlementOptions { Reason = result.FailureType?.ToString() }, cancellationToken).ConfigureAwait(false);
                settlement = MessageSettlement.Retry;
                break;
            case InboxHandlingOutcome.DeadLetter when _descriptor.Capabilities.SupportsDeadLetter:
                await message.Settlement.DeadLetterAsync(new DeadLetterOptions
                {
                    Reason = result.FailureType?.ToString(), FailureType = result.FailureType?.ToString(),
                    FailedAtUtc = _timeProvider.GetUtcNow(), Attempt = result.Attempt
                }, cancellationToken).ConfigureAwait(false);
                settlement = MessageSettlement.DeadLetter;
                break;
            case InboxHandlingOutcome.DeadLetter:
                await message.Settlement.AbandonAsync(cancellationToken).ConfigureAwait(false);
                settlement = MessageSettlement.Abandon;
                break;
            default:
                throw new InvalidOperationException($"Unsupported Inbox handling outcome '{result.Outcome}'.");
        }
        activity?.SetTag(TcjMessagingDiagnosticNames.Tags.Settlement, settlement.ToString());
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        MessagingDiagnostics.RecordSettlement(_descriptor, message.Delivery.Source, message.Envelope,
            settlement switch { MessageSettlement.Complete => "complete", MessageSettlement.Retry => "retry", MessageSettlement.DeadLetter => "dead_letter", MessageSettlement.Abandon => "abandon", _ => "defer" });
        return new InboxTransportBridgeResult(result, settlement);
    }
}
