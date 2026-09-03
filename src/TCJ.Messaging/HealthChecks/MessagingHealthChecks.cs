using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.HealthChecks;

/// <summary>Health check for the selected messaging transport.</summary>
public sealed class MessagingTransportHealthCheck : IHealthCheck
{
    private readonly MessagingTransportDescriptor[] _descriptors;
    private readonly IMessagingTransportHealthProbe[] _probes;

    /// <summary>Creates the transport health check.</summary>
    /// <param name="descriptors">Registered transport descriptors.</param>
    /// <param name="probes">Registered transport readiness probes.</param>
    public MessagingTransportHealthCheck(IEnumerable<MessagingTransportDescriptor> descriptors, IEnumerable<IMessagingTransportHealthProbe> probes)
    { _descriptors = descriptors?.ToArray() ?? throw new ArgumentNullException(nameof(descriptors)); _probes = probes?.ToArray() ?? throw new ArgumentNullException(nameof(probes)); }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_descriptors.Length != 1) return HealthCheckResult.Unhealthy("Messaging transport registration is invalid.");
        if (_probes.Length == 0) return HealthCheckResult.Healthy("Messaging transport is registered; no active probe was supplied.");
        if (_probes.Length != 1) return HealthCheckResult.Unhealthy("Messaging transport probe registration is ambiguous.");
        try { return await _probes[0].IsReadyAsync(cancellationToken).ConfigureAwait(false) ? HealthCheckResult.Healthy("Messaging transport is ready.") : HealthCheckResult.Unhealthy("Messaging transport is not ready."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return HealthCheckResult.Unhealthy("Messaging transport readiness failed."); }
    }
}

/// <summary>Health check for publisher registration.</summary>
public sealed class MessagingPublisherHealthCheck : IHealthCheck
{
    private readonly IMessagingTransportPublisher[] _publishers;
    /// <summary>Creates the publisher registration health check.</summary>
    /// <param name="publishers">Registered transport publishers.</param>
    public MessagingPublisherHealthCheck(IEnumerable<IMessagingTransportPublisher> publishers) => _publishers = publishers?.ToArray() ?? throw new ArgumentNullException(nameof(publishers));
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_publishers.Length == 1 ? HealthCheckResult.Healthy("Messaging publisher is registered.") : HealthCheckResult.Unhealthy("Messaging publisher registration is invalid.")); }
}

/// <summary>Health check for the bounded consumer loop.</summary>
public sealed class MessagingConsumerHealthCheck : IHealthCheck
{
    private readonly MessagingConsumerState _state;
    /// <summary>Creates the consumer health check.</summary>
    /// <param name="state">Shared consumer-runner health state.</param>
    public MessagingConsumerHealthCheck(MessagingConsumerState state) => _state = state ?? throw new ArgumentNullException(nameof(state));
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_state.LastFailureType is null ? HealthCheckResult.Healthy("Messaging consumer state is healthy.") : HealthCheckResult.Degraded("Messaging consumer recorded a bounded failure.")); }
}

/// <summary>Health check that reuses fail-closed messaging startup validation.</summary>
public sealed class MessagingTopologyHealthCheck : IHealthCheck
{
    private readonly IMessagingStartupValidator _validator;
    /// <summary>Creates the topology health check.</summary>
    /// <param name="validator">Messaging startup and topology validator.</param>
    public MessagingTopologyHealthCheck(IMessagingStartupValidator validator) => _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try { await _validator.ValidateAsync(cancellationToken).ConfigureAwait(false); return HealthCheckResult.Healthy("Messaging topology and registrations are valid."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return HealthCheckResult.Unhealthy("Messaging topology or registration validation failed."); }
    }
}
