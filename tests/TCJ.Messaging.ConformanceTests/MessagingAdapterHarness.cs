using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.ConformanceTests;

/// <summary>
/// Shared adapter fixture consumed by the reusable messaging conformance suite.
/// Future adapter test projects may construct this harness around their own adapter registrations.
/// </summary>
public sealed class MessagingAdapterHarness : IAsyncDisposable
{
    private readonly IAsyncDisposable? _asyncDisposable;
    private readonly IDisposable? _disposable;
    private readonly CancellationTokenSource? _scenarioCancellation;

    public MessagingAdapterHarness(
        IMessagePublisher publisher,
        IMessageBatchPublisher batchPublisher,
        IMessageReceiver receiver,
        MessagingTransportDescriptor descriptor,
        IMessagingTransportHealthProbe healthProbe,
        TimeProvider timeProvider,
        string source,
        object? lifetime = null,
        string? publishDestination = null,
        string? subscription = null,
        TimeSpan? scenarioTimeout = null)
    {
        Publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        BatchPublisher = batchPublisher ?? throw new ArgumentNullException(nameof(batchPublisher));
        Receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        HealthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Source = source;
        PublishDestination = publishDestination ?? source;
        Subscription = subscription;
        _scenarioCancellation = scenarioTimeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        _asyncDisposable = lifetime as IAsyncDisposable;
        _disposable = lifetime as IDisposable;
    }

    public IMessagePublisher Publisher { get; }
    public IMessageBatchPublisher BatchPublisher { get; }
    public IMessageReceiver Receiver { get; }
    public MessagingTransportDescriptor Descriptor { get; }
    public IMessagingTransportHealthProbe HealthProbe { get; }
    public TimeProvider TimeProvider { get; }
    public string Source { get; }
    public string PublishDestination { get; }
    public string? Subscription { get; }
    public CancellationToken ScenarioCancellation => _scenarioCancellation?.Token ?? CancellationToken.None;

    public async ValueTask DisposeAsync()
    {
        _scenarioCancellation?.Cancel();
        _scenarioCancellation?.Dispose();
        if (_asyncDisposable is not null)
        {
            await _asyncDisposable.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _disposable?.Dispose();
    }
}
