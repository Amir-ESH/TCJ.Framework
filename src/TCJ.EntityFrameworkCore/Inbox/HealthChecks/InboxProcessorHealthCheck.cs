using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Inbox;
using TCJ.EntityFrameworkCore.Inbox.Processing;

namespace TCJ.EntityFrameworkCore.Inbox.HealthChecks;

internal sealed class InboxProcessorHealthCheck(InboxProcessorState state, TcjInboxOptions options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.ProcessingMode == InboxProcessingMode.Inline) return Task.FromResult(HealthCheckResult.Healthy("Inline Inbox processing does not require a background processor."));
        if (!state.IsStarted) return Task.FromResult(HealthCheckResult.Unhealthy("Deferred Inbox processing is enabled but the processor has not started."));
        if (state.LastFailureType is { } failure) return Task.FromResult(HealthCheckResult.Degraded("Deferred Inbox processor reported a bounded failure.", data: new Dictionary<string, object> { ["failureType"] = failure }));
        return Task.FromResult(HealthCheckResult.Healthy("Deferred Inbox processor is running."));
    }
}
