using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.AzureServiceBus.Tests.Infrastructure;

internal sealed class AzureServiceBusReceivedLease(
    IAsyncEnumerator<ReceivedMessage> enumerator,
    ReceivedMessage message,
    CancellationTokenSource receiveCancellation) : IAsyncDisposable
{
    internal ReceivedMessage Message { get; } = message;

    public async ValueTask DisposeAsync()
    {
        receiveCancellation.Cancel();
        try
        {
            await enumerator.DisposeAsync();
        }
        finally
        {
            receiveCancellation.Dispose();
        }
    }
}
