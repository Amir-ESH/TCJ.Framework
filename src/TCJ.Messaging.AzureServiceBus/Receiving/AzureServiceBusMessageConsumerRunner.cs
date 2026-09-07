using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Diagnostics;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Integration;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.AzureServiceBus.Receiving;

internal sealed class AzureServiceBusMessageConsumerRunner : IMessageConsumerRunner
{
    private readonly IMessageReceiver _receiver;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessagingStartupValidator _startupValidator;
    private readonly TcjAzureServiceBusOptions _options;
    private readonly MessagingConsumerState _state;
    private readonly TimeProvider _timeProvider;

    internal AzureServiceBusMessageConsumerRunner(IMessageReceiver receiver, IServiceScopeFactory scopeFactory,
        IMessagingStartupValidator startupValidator, TcjAzureServiceBusOptions options, MessagingConsumerState state, TimeProvider timeProvider)
    { _receiver = receiver; _scopeFactory = scopeFactory; _startupValidator = startupValidator; _options = options; _state = state; _timeProvider = timeProvider; }

    public async Task RunAsync(ReceiveContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _startupValidator.ValidateAsync(cancellationToken).ConfigureAwait(false);
        _state.Start();
        using var processingCts = new CancellationTokenSource();
        var active = new HashSet<Task>();
        try
        {
            await foreach (ReceivedMessage message in _receiver.ReceiveAsync(context, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            try { await Task.WhenAll(active).WaitAsync(_options.ShutdownTimeout, _timeProvider, CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                processingCts.Cancel();
                foreach (Task task in active) _ = ObserveDetachedAsync(task);
            }
            finally { _state.Stop(); }
        }
    }

    private async Task ProcessOneAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        _state.MessageStarted();
        long started = _timeProvider.GetTimestamp();
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        InboxTransportBridge bridge = scope.ServiceProvider.GetRequiredService<InboxTransportBridge>();
        try { await bridge.ProcessAsync(message, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leave unsettled during bounded shutdown; broker redelivery plus Inbox idempotency is authoritative.
        }
        catch (Exception exception)
        {
            _state.Fail(exception.GetType().Name);
            AzureServiceBusDiagnostics.ProcessorError();
            try { await message.Settlement.RetryAsync(new RetrySettlementOptions { Reason = "UnhandledConsumerFailure" }, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            if (message.Settlement is AzureServiceBusMessageSettlement settlement)
            {
                try { await settlement.StopRenewalAsync().ConfigureAwait(false); }
                catch { }
            }
            AzureServiceBusDiagnostics.RecordProcessingDuration(_timeProvider.GetElapsedTime(started).TotalMilliseconds);
            _state.MessageStopped();
        }
    }

    private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    private static async Task ObserveDetachedAsync(Task task) { try { await task.ConfigureAwait(false); } catch { } }
}
