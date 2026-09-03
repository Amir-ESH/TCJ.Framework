using System.Diagnostics;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Diagnostics;
using TCJ.Messaging.Integration;
using TCJ.Messaging.Publishing;

namespace TCJ.Messaging.Receiving;

/// <summary>Runs a bounded transport receive loop and delegates durable processing to the Inbox bridge.</summary>
public sealed class MessageConsumerRunner : IMessageConsumerRunner
{
    private readonly IMessageReceiver _receiver;
    private readonly InboxTransportBridge _bridge;
    private readonly TcjMessagingOptions _options;
    private readonly IMessagingStartupValidator _startupValidator;
    private readonly MessagingConsumerState _state;
    private readonly MessagingTransportDescriptor _descriptor;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a bounded consumer runner.</summary>
    /// <param name="receiver">Selected transport receiver.</param>
    /// <param name="bridge">Transactional Inbox bridge.</param>
    /// <param name="options">Messaging bounds.</param>
    /// <param name="startupValidator">Fail-closed startup validator.</param>
    /// <param name="state">Consumer readiness state.</param>
    /// <param name="descriptor">Selected transport descriptor.</param>
    /// <param name="timeProvider">Deterministic time provider.</param>
    public MessageConsumerRunner(
        IMessageReceiver receiver,
        InboxTransportBridge bridge,
        TcjMessagingOptions options,
        IMessagingStartupValidator startupValidator,
        MessagingConsumerState state,
        MessagingTransportDescriptor descriptor,
        TimeProvider timeProvider)
    {
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _startupValidator = startupValidator ?? throw new ArgumentNullException(nameof(startupValidator));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task RunAsync(ReceiveContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _startupValidator.ValidateAsync(cancellationToken).ConfigureAwait(false);
        _state.Start();
        MessagingDiagnostics.ConsumerStarted();
        using var processingCts = new CancellationTokenSource();
        var active = new HashSet<Task>();
        try
        {
            await foreach (ReceivedMessage message in _receiver.ReceiveAsync(context, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                while (active.Count >= _options.MaximumConcurrentMessages)
                {
                    Task completed = await Task.WhenAny(active).ConfigureAwait(false);
                    active.Remove(completed);
                    await ObserveAsync(completed).ConfigureAwait(false);
                }

                active.Add(ProcessOneAsync(message, processingCts.Token));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await Task.WhenAll(active)
                    .WaitAsync(_options.ShutdownTimeout, _timeProvider, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (TimeoutException)
            {
                processingCts.Cancel();
                foreach (Task task in active)
                    _ = ObserveDetachedAsync(task);
            }
            finally
            {
                _state.Stop();
                MessagingDiagnostics.ConsumerStopped();
            }
        }
    }

    private async Task ProcessOneAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        _state.MessageStarted();
        MessagingDiagnostics.RecordReceived(_descriptor, message.Delivery.Source, message.Envelope);
        using Activity? activity = MessagingDiagnostics.StartReceive(message.Envelope, _descriptor, message.Delivery.Source);
        using Activity? execution = MessagingDiagnostics.StartConsumerExecute(message.Envelope, _descriptor, message.Delivery.Source);
        long started = _timeProvider.GetTimestamp();
        try
        {
            InboxTransportBridgeResult result = await _bridge.ProcessAsync(message, cancellationToken).ConfigureAwait(false);
            string outcome = result.InboxResult.Outcome.ToString();
            execution?.SetTag(TcjMessagingDiagnosticNames.Tags.Outcome, outcome);
            execution?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetStatus(ActivityStatusCode.Ok);
            MessagingDiagnostics.RecordProcessingDuration(
                _descriptor,
                message.Delivery.Source,
                message.Envelope,
                outcome,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            execution?.SetTag(TcjMessagingDiagnosticNames.Tags.Canceled, true);
            execution?.SetStatus(ActivityStatusCode.Error, "canceled");
            activity?.SetStatus(ActivityStatusCode.Error, "canceled");
            throw;
        }
        catch (Exception exception)
        {
            string failureType = exception.GetType().Name;
            _state.Fail(failureType);
            execution?.SetTag(TcjMessagingDiagnosticNames.Tags.FailureType, failureType);
            execution?.SetStatus(ActivityStatusCode.Error, failureType);
            activity?.SetStatus(ActivityStatusCode.Error, failureType);
        }
        finally
        {
            _state.MessageStopped();
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
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
