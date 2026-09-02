using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TCJ.Core.Inbox;

namespace TCJ.AspNetCore.Inbox;

internal sealed class InboxHostedService(
    IServiceScopeFactory scopeFactory,
    TcjInboxOptions options,
    TimeProvider timeProvider,
    ILogger<InboxHostedService> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> LogPollFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(0), "TCJ Inbox processing poll failed with failure type {FailureType}. The next bounded poll will retry eligible work.");
    private DateTimeOffset _nextCleanupAtUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        if (options.ProcessingMode != InboxProcessingMode.Deferred) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            bool hadWork = false;
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                InboxProcessingResult result = await scope.ServiceProvider.GetRequiredService<IInboxDeferredProcessor>().ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
                hadWork = result.HasWork;
                DateTimeOffset now = timeProvider.GetUtcNow();
                if (options.RetentionPeriod > TimeSpan.Zero && now >= _nextCleanupAtUtc)
                {
                    await scope.ServiceProvider.GetRequiredService<IInboxCleanupService>().CleanupAsync(stoppingToken).ConfigureAwait(false);
                    _nextCleanupAtUtc = now + options.CleanupInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                LogPollFailed(logger, NormalizeType(exception.GetType()), null);
                hadWork = false;
            }
            if (!hadWork) await Task.Delay(options.PollingInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    private static string NormalizeType(Type type)
    {
        string value = type.FullName ?? type.Name;
        int marker = value.IndexOf('`');
        return (marker >= 0 ? value[..marker] : value).Replace('+', '.');
    }
}
