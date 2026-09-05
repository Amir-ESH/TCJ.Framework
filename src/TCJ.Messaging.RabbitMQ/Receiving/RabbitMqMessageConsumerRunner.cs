using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Integration;
using TCJ.Messaging.Receiving;
using TCJ.Messaging.RabbitMQ.Configuration;
using TCJ.Messaging.RabbitMQ.Diagnostics;

namespace TCJ.Messaging.RabbitMQ.Receiving;

internal sealed class RabbitMqMessageConsumerRunner : IMessageConsumerRunner
{
    private readonly IMessageReceiver _receiver;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessagingStartupValidator _startupValidator;
    private readonly TcjRabbitMqOptions _options;
    private readonly MessagingConsumerState _state;
    private readonly TimeProvider _timeProvider;

    internal RabbitMqMessageConsumerRunner(IMessageReceiver receiver, IServiceScopeFactory scopeFactory,
        IMessagingStartupValidator startupValidator, TcjRabbitMqOptions options, MessagingConsumerState state, TimeProvider timeProvider)
    {
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _startupValidator = startupValidator ?? throw new ArgumentNullException(nameof(startupValidator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task RunAsync(ReceiveContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _startupValidator.ValidateAsync(cancellationToken).ConfigureAwait(false);
        _state.Start();
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
                await Task.WhenAll(active).WaitAsync(_options.ShutdownTimeout, _timeProvider, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                processingCts.Cancel();
                foreach (Task task in active) _ = ObserveDetachedAsync(task);
            }
            finally
            {
                _state.Stop();
            }
        }
    }

    private async Task ProcessOneAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        _state.MessageStarted();
        long started = _timeProvider.GetTimestamp();
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        InboxTransportBridge bridge = scope.ServiceProvider.GetRequiredService<InboxTransportBridge>();
        try
        {
            await bridge.ProcessAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leave the delivery unsettled; receiver shutdown waits boundedly and then closes the channel so RabbitMQ can redeliver it.
        }
        catch (Exception exception)
        {
            _state.Fail(exception.GetType().Name);
            try
            {
                await message.Settlement.RetryAsync(new RetrySettlementOptions { Reason = "UnhandledConsumerFailure" }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Do not falsely acknowledge. Closing the consumer channel makes the delivery eligible for broker redelivery.
            }
        }
        finally
        {
            RabbitMqDiagnostics.RecordProcessingDuration(_timeProvider.GetElapsedTime(started).TotalMilliseconds);
            _state.MessageStopped();
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }
    private static async Task ObserveDetachedAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { }
    }
}
