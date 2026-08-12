using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TCJ.Core.Outbox;

namespace TCJ.AspNetCore.Outbox;

internal sealed class OutboxHostedService(
    IServiceScopeFactory scopeFactory,
    TcjOutboxOptions options,
    TimeProvider timeProvider,
    ILogger<OutboxHostedService> logger) : BackgroundService
{
    private DateTimeOffset _nextCleanupAtUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();

        while (!stoppingToken.IsCancellationRequested)
        {
            bool hadWork = false;
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                IOutboxProcessor processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
                OutboxProcessingResult result = await processor.ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
                hadWork = result.HasWork;

                DateTimeOffset now = timeProvider.GetUtcNow();
                if (options.RetentionPeriod > TimeSpan.Zero && now >= _nextCleanupAtUtc)
                {
                    IOutboxCleanupService cleanup = scope.ServiceProvider.GetRequiredService<IOutboxCleanupService>();
                    await cleanup.CleanupAsync(stoppingToken).ConfigureAwait(false);
                    _nextCleanupAtUtc = now + options.CleanupInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Deliberately avoid logging exception messages because third-party exceptions can contain serialized data.
                logger.LogError("TCJ outbox processing poll failed with failure type {FailureType}. The next bounded poll will retry eligible work.", NormalizeType(exception.GetType()));
                hadWork = false;
            }

            if (!hadWork)
            {
                await Task.Delay(options.PollingInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static string NormalizeType(Type type)
    {
        string value = type.FullName ?? type.Name;
        int genericMarker = value.IndexOf('`');
        return (genericMarker >= 0 ? value[..genericMarker] : value).Replace('+', '.');
    }
}
