using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.RabbitMQ.Configuration;
using TCJ.Messaging.RabbitMQ.Connections;

namespace TCJ.Messaging.RabbitMQ.HealthChecks;

/// <summary>Stable RabbitMQ adapter readiness health-check names.</summary>
public static class TcjRabbitMqHealthCheckNames
{
    /// <summary>Connection readiness check.</summary>
    public const string Connection = "tcj.rabbitmq.connection";
    /// <summary>Publisher readiness check.</summary>
    public const string Publisher = "tcj.rabbitmq.publisher";
    /// <summary>Consumer registration/readiness check.</summary>
    public const string Consumer = "tcj.rabbitmq.consumer";
    /// <summary>Topology/startup validation check.</summary>
    public const string Topology = "tcj.rabbitmq.topology";
}

internal sealed class RabbitMqConnectionHealthCheck : IHealthCheck
{
    private readonly RabbitMqConnectionManager _connections;
    private readonly TcjRabbitMqOptions _options;
    internal RabbitMqConnectionHealthCheck(RabbitMqConnectionManager connections, TcjRabbitMqOptions options)
    { _connections = connections; _options = options; }
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.ConnectionTimeout);
        try
        {
            _ = await _connections.GetConnectionAsync(cts.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy("RabbitMQ connection is ready.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return HealthCheckResult.Unhealthy("RabbitMQ connection is not ready."); }
    }
}

internal sealed class RabbitMqPublisherHealthCheck : IHealthCheck
{
    private readonly RabbitMqConnectionManager _connections;
    internal RabbitMqPublisherHealthCheck(RabbitMqConnectionManager connections) => _connections = connections;
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_connections.IsOpen ? HealthCheckResult.Healthy("RabbitMQ publisher connection is open.") : HealthCheckResult.Degraded("RabbitMQ publisher connection is not currently open."));
    }
}

internal sealed class RabbitMqConsumerHealthCheck : IHealthCheck
{
    private readonly TcjMessagingOptions _messaging;
    private readonly TcjRabbitMqOptions _rabbit;
    internal RabbitMqConsumerHealthCheck(TcjMessagingOptions messaging, TcjRabbitMqOptions rabbit) { _messaging = messaging; _rabbit = rabbit; }
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        if (!_messaging.EnableConsumer) return Task.FromResult(HealthCheckResult.Healthy("RabbitMQ consumer processing is disabled."));
        bool configured = _rabbit.Topology.Queues.Count > 0 && _rabbit.Topology.RetryTopologies.Count > 0;
        return Task.FromResult(configured ? HealthCheckResult.Healthy("RabbitMQ consumer topology is configured.") : HealthCheckResult.Unhealthy("RabbitMQ consumer topology is incomplete."));
    }
}

internal sealed class RabbitMqTopologyHealthCheck : IHealthCheck
{
    private readonly IMessagingStartupValidator _validator;
    internal RabbitMqTopologyHealthCheck(IMessagingStartupValidator validator) => _validator = validator;
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try { await _validator.ValidateAsync(cancellationToken).ConfigureAwait(false); return HealthCheckResult.Healthy("RabbitMQ topology is valid."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return HealthCheckResult.Unhealthy("RabbitMQ topology validation failed."); }
    }
}
