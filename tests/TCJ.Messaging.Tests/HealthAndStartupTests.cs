using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.InMemory;

namespace TCJ.Messaging.Tests;

public sealed class HealthAndStartupTests
{
    [Fact]
    public async Task Messaging_health_checks_register_stable_names_and_report_healthy_in_memory_transport()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjInMemoryMessaging();
        services.AddHealthChecks().AddTcjMessagingHealthChecks();
        using ServiceProvider provider = services.BuildServiceProvider();

        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(4, report.Entries.Count);
        Assert.Equal(HealthStatus.Healthy, report.Entries[TcjMessagingHealthCheckNames.Transport].Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries[TcjMessagingHealthCheckNames.Publisher].Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries[TcjMessagingHealthCheckNames.Consumer].Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries[TcjMessagingHealthCheckNames.Topology].Status);
    }

    [Fact]
    public async Task Messaging_transport_health_check_becomes_unhealthy_when_probe_is_unavailable()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjInMemoryMessaging();
        services.AddHealthChecks().AddTcjMessagingHealthChecks();
        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<InMemoryMessagingTransport>().IsAvailable = false;

        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Unhealthy, report.Entries[TcjMessagingHealthCheckNames.Transport].Status);
    }

    [Fact]
    public async Task Messaging_startup_validator_fails_closed_without_transport_registration()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        using ServiceProvider provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<IMessagingStartupValidator>().ValidateAsync());
    }
}
