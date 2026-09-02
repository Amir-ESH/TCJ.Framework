using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;

namespace TCJ.Messaging.Publishing;

/// <summary>Policy-enforcing bounded batch publisher.</summary>
public sealed class MessageBatchPublisher : IMessageBatchPublisher
{
    private readonly IMessagingTransportBatchPublisher? _transport;
    private readonly MessagingTransportDescriptor _descriptor;
    private readonly IMessagingStartupValidator _startupValidator;
    private readonly MessagePublisher _singlePublisher;
    private readonly TcjMessagingOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the bounded batch publisher.</summary>
    /// <param name="transports">Registered native batch publishers.</param><param name="descriptor">Transport descriptor.</param>
    /// <param name="startupValidator">Startup validator.</param><param name="singlePublisher">Central single-message policy.</param><param name="options">Messaging options.</param>
    /// <param name="timeProvider">Time source used for deterministic publish timeouts.</param>
    public MessageBatchPublisher(IEnumerable<IMessagingTransportBatchPublisher> transports, MessagingTransportDescriptor descriptor,
        IMessagingStartupValidator startupValidator, MessagePublisher singlePublisher, TcjMessagingOptions options, TimeProvider timeProvider)
    {
        IMessagingTransportBatchPublisher[] array = transports?.Take(2).ToArray() ?? throw new ArgumentNullException(nameof(transports));
        if (array.Length > 1) throw new InvalidOperationException("Only one default messaging batch publisher may be registered.");
        _transport = array.SingleOrDefault();
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _startupValidator = startupValidator ?? throw new ArgumentNullException(nameof(startupValidator));
        _singlePublisher = singlePublisher ?? throw new ArgumentNullException(nameof(singlePublisher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<TransportMessageEnvelope> messages,
        PublishContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        await _startupValidator.ValidateAsync(cancellationToken).ConfigureAwait(false);
        if (messages.Count == 0) return [];
        int maximumBatchSize = _descriptor.Capabilities.MaximumBatchSize ?? 1000;
        if (messages.Count > maximumBatchSize)
            throw new ArgumentOutOfRangeException(nameof(messages), $"Batch size exceeds the adapter limit of {maximumBatchSize} messages.");
        if (!_descriptor.Capabilities.SupportsBatchPublish || _transport is null)
            return Enumerable.Range(0, messages.Count).Select(static _ => PublishResult.Unsupported("BatchPublish")).ToArray();

        var results = new PublishResult[messages.Count];
        var prepared = new List<TransportMessageEnvelope>(messages.Count);
        var preparedIndices = new List<int>(messages.Count);
        PublishContext? effectiveContext = null;
        for (int index = 0; index < messages.Count; index++)
        {
            TransportMessageEnvelope message = messages[index] ?? throw new ArgumentException("Batch messages cannot contain null entries.", nameof(messages));
            PublishResult? failure = _singlePublisher.TryPrepare(message, context, out PreparedPublish? item);
            if (failure is not null) { results[index] = failure; continue; }
            if (effectiveContext is null) effectiveContext = item!.Context;
            else if (!string.Equals(effectiveContext.Destination, item!.Context.Destination, StringComparison.Ordinal))
            { results[index] = PublishResult.Unsupported("MixedBatchDestinations"); continue; }
            prepared.Add(item.Message);
            preparedIndices.Add(index);
        }
        if (prepared.Count == 0) return results;

        using var transportCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IReadOnlyList<PublishResult>> batchTask = _transport.PublishBatchAsync(prepared, effectiveContext!, transportCts.Token);
        IReadOnlyList<PublishResult> adapterResults;
        try
        {
            adapterResults = await batchTask
                .WaitAsync(_options.PublishTimeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            transportCts.Cancel();
            _ = ObserveDetachedAsync(batchTask);
            adapterResults = Enumerable.Range(0, prepared.Count).Select(static _ =>
                new PublishResult(PublishOutcome.Canceled, FailureCategory: MessagingFailureCategory.Canceled, FailureType: nameof(MessagingFailureCategory.Canceled))).ToArray();
        }
        catch (TimeoutException)
        {
            transportCts.Cancel();
            _ = ObserveDetachedAsync(batchTask);
            adapterResults = Enumerable.Range(0, prepared.Count).Select(static _ =>
                new PublishResult(PublishOutcome.TimedOut, FailureCategory: MessagingFailureCategory.TransientTimeout, FailureType: nameof(MessagingFailureCategory.TransientTimeout))).ToArray();
        }
        if (adapterResults.Count != prepared.Count)
            throw new InvalidOperationException("A messaging batch publisher must return exactly one result for each submitted message.");
        for (int index = 0; index < adapterResults.Count; index++) results[preparedIndices[index]] = adapterResults[index];
        return results;
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
