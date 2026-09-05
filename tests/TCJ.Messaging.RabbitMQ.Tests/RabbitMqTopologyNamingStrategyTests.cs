using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.RabbitMQ.Extensions;
using TCJ.Messaging.RabbitMQ.Topology;
using TCJ.Messaging.Topology;

namespace TCJ.Messaging.RabbitMQ.Tests;

public sealed class RabbitMqTopologyNamingStrategyTests
{
    [Theory]
    [InlineData("orders-worker")]
    public void RabbitMq_registration_resolves_complete_topology_naming_contract_without_connecting(string consumerName)
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjRabbitMq(options => options.TopologyMode = RabbitMqTopologyMode.Disabled);

        using ServiceProvider provider = services.BuildServiceProvider();
        IMessageTopologyNamingStrategy naming = provider.GetRequiredService<IMessageTopologyNamingStrategy>();

        Assert.Equal("tcj.events", naming.GetDestination("order.submitted", 1));
        Assert.Equal(consumerName, naming.GetSubscription(consumerName));
        Assert.Throws<ArgumentException>(() => naming.GetSubscription("amq.reserved"));
    }
}
