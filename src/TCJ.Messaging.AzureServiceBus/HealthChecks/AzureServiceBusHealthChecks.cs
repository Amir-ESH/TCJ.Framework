using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Connections;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.AzureServiceBus.HealthChecks;

/// <summary>Stable Azure Service Bus adapter readiness check names.</summary>
public static class TcjAzureServiceBusHealthCheckNames
{
    public const string Client = "tcj.azure_service_bus.client";
    public const string Sender = "tcj.azure_service_bus.sender";
    public const string Processor = "tcj.azure_service_bus.processor";
    public const string Topology = "tcj.azure_service_bus.topology";
    public const string SessionProcessor = "tcj.azure_service_bus.session_processor";
}

internal sealed class AzureServiceBusClientHealthCheck : IHealthCheck
{
    private readonly AzureServiceBusClientManager _clients;
    private readonly TcjAzureServiceBusOptions _options;
    internal AzureServiceBusClientHealthCheck(AzureServiceBusClientManager clients, TcjAzureServiceBusOptions options) { _clients = clients; _options = options; }
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.TryTimeout);
        try { bool ready = await _clients.ProbeReadinessAsync(cts.Token).ConfigureAwait(false); return ready ? HealthCheckResult.Healthy("Azure Service Bus client is ready for broker operations.") : HealthCheckResult.Unhealthy("Azure Service Bus client is not ready."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return HealthCheckResult.Unhealthy("Azure Service Bus client is not ready."); }
    }
}

internal sealed class AzureServiceBusSenderHealthCheck : IHealthCheck
{
    private readonly AzureServiceBusClientManager _clients;
    internal AzureServiceBusSenderHealthCheck(AzureServiceBusClientManager clients) => _clients = clients;
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_clients.IsClientOpen ? HealthCheckResult.Healthy("Azure Service Bus shared client is open for sender creation.") : HealthCheckResult.Degraded("Azure Service Bus client has not opened yet."));
    }
}

internal sealed class AzureServiceBusProcessorHealthCheck : IHealthCheck
{
    private readonly TcjMessagingOptions _messaging;
    private readonly MessagingConsumerState _state;
    internal AzureServiceBusProcessorHealthCheck(TcjMessagingOptions messaging, MessagingConsumerState state) { _messaging = messaging; _state = state; }
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        if (!_messaging.EnableConsumer) return Task.FromResult(HealthCheckResult.Healthy("Azure Service Bus consumer processing is disabled."));
        if (_state.LastFailureType is not null) return Task.FromResult(HealthCheckResult.Unhealthy("Azure Service Bus consumer reported a bounded processing failure."));
        return Task.FromResult(_state.IsRunning ? HealthCheckResult.Healthy("Azure Service Bus consumer is running.") : HealthCheckResult.Degraded("Azure Service Bus consumer is configured but not currently running."));
    }
}

internal sealed class AzureServiceBusTopologyHealthCheck : IHealthCheck
{
    private readonly IMessagingStartupValidator _validator;
    internal AzureServiceBusTopologyHealthCheck(IMessagingStartupValidator validator) => _validator = validator;
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try { await _validator.ValidateAsync(cancellationToken).ConfigureAwait(false); return HealthCheckResult.Healthy("Azure Service Bus topology/startup contract is valid."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return HealthCheckResult.Unhealthy("Azure Service Bus topology/startup validation failed."); }
    }
}

internal sealed class AzureServiceBusSessionProcessorHealthCheck : IHealthCheck
{
    private readonly TcjAzureServiceBusOptions _options;
    internal AzureServiceBusSessionProcessorHealthCheck(TcjAzureServiceBusOptions options) => _options = options;
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        bool hasSessionTopology = _options.Topology.Queues.Any(static q => q.RequiresSession) || _options.Topology.Subscriptions.Any(static s => s.RequiresSession);
        return Task.FromResult(!hasSessionTopology || _options.MaximumConcurrentSessions > 0
            ? HealthCheckResult.Healthy("Azure Service Bus session processing configuration is bounded and valid.")
            : HealthCheckResult.Unhealthy("Azure Service Bus session processing configuration is invalid."));
    }
}
