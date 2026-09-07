using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Connections;
using TCJ.Messaging.AzureServiceBus.Extensions;
using TCJ.Messaging.AzureServiceBus.HealthChecks;
using TCJ.Messaging.Extensions;

namespace TCJ.HealthChecks.Tests.Tests;

[Trait("Category", "HealthChecks")]
[Trait("Category", "AzureServiceBus")]
public sealed class AzureServiceBusHealthCheckTests
{
    [Fact]
    public void Readiness_registration_is_complete_idempotent_and_never_tagged_as_liveness()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjMessaging();
        services.AddTcjAzureServiceBus("Endpoint=sb://localhost/;SharedAccessKeyName=local;SharedAccessKey=local");
        IHealthChecksBuilder health = services.AddHealthChecks();
        health.AddTcjAzureServiceBusHealthChecks();
        health.AddTcjAzureServiceBusHealthChecks();
        using ServiceProvider provider = services.BuildServiceProvider();

        HealthCheckRegistration[] registrations = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations
            .Where(static registration => registration.Tags.Contains("azure-service-bus"))
            .ToArray();

        string[] expected =
        [
            TcjAzureServiceBusHealthCheckNames.Client,
            TcjAzureServiceBusHealthCheckNames.Sender,
            TcjAzureServiceBusHealthCheckNames.Processor,
            TcjAzureServiceBusHealthCheckNames.Topology,
            TcjAzureServiceBusHealthCheckNames.SessionProcessor
        ];
        Assert.Equal(expected.OrderBy(static value => value), registrations.Select(static registration => registration.Name).OrderBy(static value => value));
        Assert.Equal(expected.Length, registrations.Length);
        Assert.All(registrations, registration =>
        {
            Assert.Contains("ready", registration.Tags);
            Assert.DoesNotContain("live", registration.Tags);
        });
    }

    [Fact]
    public async Task Client_failure_response_does_not_expose_exception_or_credential_text()
    {
        const string secret = "TCJ-HEALTH-CREDENTIAL-SECRET";
        var options = new TcjAzureServiceBusOptions
        {
            ConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=local;SharedAccessKey=local",
            TryTimeout = TimeSpan.FromSeconds(1)
        };
        await using var manager = new AzureServiceBusClientManager(
            options,
            new AzureServiceBusAuthentication(null),
            new ThrowingClientFactory(new UnauthorizedAccessException(secret)));
        var check = new AzureServiceBusClientHealthCheck(manager, options);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.DoesNotContain(secret, result.Description ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(result.Exception);
    }

    [Fact]
    public async Task Session_processor_health_is_healthy_for_bounded_session_configuration()
    {
        var options = new TcjAzureServiceBusOptions { MaximumConcurrentSessions = 4 };
        options.Topology.Queues.Add(new TCJ.Messaging.AzureServiceBus.Topology.AzureServiceBusQueueOptions
        {
            Name = "orders-session",
            RequiresSession = true
        });
        var check = new AzureServiceBusSessionProcessorHealthCheck(options);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private sealed class ThrowingClientFactory(Exception failure) : IAzureServiceBusClientFactory
    {
        public ServiceBusClient CreateClient(TcjAzureServiceBusOptions options, AzureServiceBusAuthentication authentication) => throw failure;

        public ServiceBusAdministrationClient CreateAdministrationClient(
            TcjAzureServiceBusOptions options,
            AzureServiceBusAuthentication authentication) => throw failure;
    }
}
