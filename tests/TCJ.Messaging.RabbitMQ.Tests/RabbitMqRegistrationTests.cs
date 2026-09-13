using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Inbox;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.HealthChecks;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.RabbitMQ.Extensions;
using TCJ.Messaging.RabbitMQ.Tests.Infrastructure;
using TCJ.Messaging.RabbitMQ.Topology;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.RabbitMQ.Tests;

public sealed class RabbitMqRegistrationTests
{
    [Fact]
    public async Task Canceled_readiness_never_reports_success_or_connects()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjRabbitMq(o => o.TopologyMode = RabbitMqTopologyMode.Disabled);
        await using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRequiredService<IMessagingTransportHealthProbe>().IsReadyAsync(cancellation.Token).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registered_services_can_be_constructed_without_broker_access(bool consumer)
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging(o => o.EnableConsumer = consumer);
        services.AddSingleton(new TcjInboxOptions { ConsumerName = "registration" });
        services.AddSingleton<IInboxPipeline>(new StubInboxPipeline((_, _) => Task.FromResult(new InboxHandlingResult(InboxHandlingOutcome.Acknowledge))));
        services.AddTcjRabbitMq(o => o.TopologyMode = RabbitMqTopologyMode.Disabled);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.NotNull(provider.GetRequiredService<IMessagePublisher>());
        Assert.NotNull(provider.GetRequiredService<IMessageReceiver>());
        Assert.NotNull(provider.GetRequiredService<IMessagingStartupValidator>());
        Assert.NotNull(provider.GetRequiredService<IMessagingTransportHealthProbe>());
        if (consumer) Assert.NotNull(provider.GetRequiredService<IMessageConsumerRunner>());
        foreach (var registration in services.Where(r => typeof(IHealthCheck).IsAssignableFrom(r.ServiceType)))
            Assert.NotNull(provider.GetRequiredService(registration.ServiceType));
    }
}
